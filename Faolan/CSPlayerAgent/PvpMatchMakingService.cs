using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Pvp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Faolan.CSPlayerAgent
{
	// The PvP minigame QUEUE (#882), built message-for-message from the capture that walks it end to end
	// (`NecroL80_mage-necromancer_login_gameplay_annotated_group-finder_pvp-matchmaking`, t=1057..1150 - seven signups
	// and four removals, each one annotated by the player as they made it):
	//
	//   c PvPMatchMakingPutIntoMatchMaking(map, type, bringTeam, queueType)
	//                                     -> s DispatchPvPMatchMakingYouAreInMatchMaking(the request + two flags)
	//   c PvPMatchMakingRemoveFromMatchMaking()   -> s the empty 0x2081 ack
	//   c PvPMatchMakingRequestInfo(queueType)    -> s DispatchPvPMatchMakingInfoRequestReturn(the live board)
	//   c PvPMatchMakingIsInMatchMaking()         -> retail answers with NO PACKET AT ALL
	//
	// ⭐ A character holds exactly ONE signup. That is not an assumption: every `PutIntoMatchMaking` in the capture
	// raises its own map's count by one AND drops the previously chosen map's count by one, in the same board, and
	// `PvPMatchMakingRemoveFromMatchMaking` takes no argument - there is only ever one thing to remove.
	//
	// ⛔ MATCHMAKING PROPER IS NOT MODELLED, and deliberately so. Forming a match out of the queue would mean
	// sending `DispatchPvPMatchMakingWantsToStart` / `...YouJoinedMatch` / `...StartClicked` and running a minigame
	// instance - and `PvPMatchRules_c`, the element three of those carry, has ZERO captured instances and an
	// unpinned column layout (docs/re/minigame-massivepvp-messages.md, `[V0: the PvPMatchRules_c element layout]`).
	// What is built here is the whole of what the corpus shows: the queue, its board, and the acks. A player who
	// signs up stays signed up and the counter moves; nothing pops.
	public sealed class PvpMatchMakingService(ILogger<CsPlayerAgentListener> logger, TeamService teams, [FromKeyedServices(CsPlayerAgentServices.ConnectionsKey)] ConnectionRegistry connections)
	{
		// characterId -> the one signup it holds. Removed by RemoveFromMatchMaking and by the disconnect hook, so a
		// logged-off queuer stops being counted (retail's board is live state, and a stale row is a visible lie).
		// The sequence makes Queued() FIFO: a ConcurrentDictionary enumerates in ARBITRARY order, and the match
		// formation's anchor (and so the formed match's leader) is meant to be the longest-queued signup.
		private readonly ConcurrentDictionary<uint, (long Seq, PvpMatchMakingSignup Signup)> _signups = [];

		private long _nextSeq;

		// Every (type, map, queueType) anyone has signed up for since this process started. Retail keeps listing a
		// queue at 0 once it has appeared - the capture's board still carries `The Call of Jhebbal Sag` at zero long
		// after the player dequeued it - so the registry is separate from the counts and only ever grows.
		private readonly ConcurrentDictionary<(uint MinigameType, uint MapId, uint QueueType), byte> _knownQueues = [];

		/// <summary>c2s 0x2052: sign up. Replaces whatever signup this character held.</summary>
		public async Task HandlePutIntoMatchMaking(INetworkClient client, PvpMatchMakingSignup signup)
		{
			var characterId = client.Session.Character.Id;

			Record(characterId, signup);
			await Send(client, PvpMatchMakingStatusCodec.Method, PvpMatchMakingStatusCodec.Build(signup, false));

			if (signup.IsAnyAvailable)
				logger.LogInformation("PvP matchmaking: {CharacterId:X8} signed up for the first available minigame.", characterId);
			else
				logger.LogInformation("PvP matchmaking: {CharacterId:X8} signed up for map {MapId} (type {Type}, queue {Queue}).", characterId, signup.MapId, signup.MinigameType,
					signup.QueueType);

			if (!signup.BringTeam)
				return;

			// `i_bBringTeam` is 0 in every captured signup, so the corpus shows only the solo side of this. It is a
			// real field the client sets from its own checkbox though, and ignoring it would silently queue one
			// character where the player asked for six. Each member is queued for the same minigame and gets the
			// SAME acknowledgement message with `i_AsTeamMember` set - which is the one field on that message whose
			// purpose is exactly this. No uncaptured message shape goes out.
			var members = teams.TeamOf(characterId) is { } teamId
				? teams.MembersOf(teamId).Where(m => m != characterId).ToList()
				: [];

			if (members.Count == 0)
			{
				logger.LogInformation("PvP matchmaking: {CharacterId:X8} asked to bring its team, but it is not in one; the signup stands alone.", characterId);
				return;
			}

			foreach (var member in members)
			{
				Record(member, signup);
				if (connections.ForCharacter(member) is { } memberClient)
					await Send(memberClient, PvpMatchMakingStatusCodec.Method, PvpMatchMakingStatusCodec.Build(signup, true));
			}

			logger.LogInformation("PvP matchmaking: {CharacterId:X8} brought {Count} team member(s) into the queue.", characterId, members.Count);
		}

		/// <summary>c2s 0x2053: withdraw. Answered by the empty 0x2081, ~0.3 s later in all four captured removals.</summary>
		public async Task HandleRemoveFromMatchMaking(INetworkClient client)
		{
			var characterId = client.Session.Character.Id;
			if (_signups.TryRemove(characterId, out _))
				logger.LogInformation("PvP matchmaking: {CharacterId:X8} left the queue.", characterId);
			else
				logger.LogDebug("PvP matchmaking: {CharacterId:X8} left the queue while holding no signup.", characterId);

			// ⭐ The ack is named now (#876): 0x81's own override raises the feedback token
			// `YouAreNoLongerInPvPMatchMaking`, which is exactly what this is - the answer to a withdrawal. It was
			// sent as `Reserved0x81` on the strength of being 1:1 with the removals in the capture and empty every
			// time; the recovered name says the same thing from the client's side, which is the corroboration that
			// reading never had.
			await Send(client, (uint)CSPlayerInterfaceMethods.DispatchPvPMatchMakingRemovedFromMatchMaking, []);
		}

		/// <summary>c2s 0x2054: the board poll. The reply is the same for every queueType the client asks about.</summary>
		public async Task HandleRequestInfo(INetworkClient client, uint queueType)
		{
			// Every captured request carries queueType 0 and every reply carries BOTH the type-4 and the type-5
			// groups, so the board is not filtered by the argument - it is the whole board. Logged rather than
			// applied, so a request for another queue type shows up if one ever appears.
			if (queueType != 0)
				logger.LogDebug("PvPMatchMakingRequestInfo asked about queue type {QueueType}; retail's reply is the whole board regardless.", queueType);

			await Send(client, PvpMatchMakingInfoCodec.Method, PvpMatchMakingInfoCodec.Build(Board()));
		}

		/// <summary>The live board: the sticky queue registry, counted against the signups held right now.</summary>
		public PvpMatchMakingInfo Board()
		{
			var held = _signups.Values.Select(v => v.Signup).ToList();

			return PvpMatchMakingBoard.From(
				_knownQueues.Keys,
				held.Where(s => !s.IsAnyAvailable).Select(s => (s.MinigameType, s.MapId, s.QueueType)),
				(uint)held.Count(s => s.IsAnyAvailable));
		}

		/// <summary>
		///     The live signups, for the match-formation pass (#146): PvpMatchService pairs compatible ones after
		///     each new signup. Snapshot semantics - formation dequeues through <see cref="Forget" />.
		/// </summary>
		public IReadOnlyList<(uint CharacterId, PvpMatchMakingSignup Signup)> Queued()
		{
			return _signups.OrderBy(kv => kv.Value.Seq).Select(kv => (kv.Key, kv.Value.Signup)).ToList();
		}

		/// <summary>
		///     Drop a character's signup: the disconnect hook, and the dequeue when a formed match consumes it - the
		///     board stops counting someone who is gone either way.
		/// </summary>
		public void Forget(uint characterId)
		{
			if (_signups.TryRemove(characterId, out _))
				logger.LogDebug("PvP matchmaking: dropped {CharacterId:X8}'s signup (disconnect or match formation).", characterId);
		}

		private void Record(uint characterId, PvpMatchMakingSignup signup)
		{
			_signups[characterId] = (System.Threading.Interlocked.Increment(ref _nextSeq), signup);

			// A "join first available" signup is counted in the board's NumberOfAnyApplies, not listed as a queue -
			// the capture never grows a type-0 group when the player checks that box.
			if (!signup.IsAnyAvailable)
				_knownQueues.TryAdd((signup.MinigameType, signup.MapId, signup.QueueType), 0);
		}

		private static Task Send(INetworkClient client, uint method, byte[] body)
		{
			return TeamService.SendToClient(client, (CSPlayerInterfaceMethods)method, body);
		}
	}
}
