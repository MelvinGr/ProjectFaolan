using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Enums;
using Faolan.Core;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Pvp;
using Faolan.Core.Network.Codecs.Team;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Server.CsPlayerAgent;
using Faolan.Core.Network.Server.GameServer;
using Microsoft.Extensions.Logging;

namespace Faolan.CSPlayerAgent
{
	// CUSTOM PvP matches (#146): the create/join/side/start lifecycle the client's own match-buildup GUI drives,
	// on top of the queue (PvpMatchMakingService). The message set is fully recovered from the client
	// (docs/re/minigame-massivepvp-messages.md); ⚠️ none of it is captured - no captured session ever creates or
	// fills a match - so every body here is built from the client-recovered layout, and the whole feature carries
	// #146's needs-testing until a retail client walks it. What the corpus DOES pin is deliberately not repeated
	// here: the queue half stays in PvpMatchMakingService, byte-matched to its capture.
	//
	// The shape mirrors retail's own flow as the client tells it:
	//   c CreateMatch(rules)  -> s YouJoinedMatch(match) to the creator (their client raises "YouCreatedMatch"
	//                            itself when the match's leader ID32 is them - the fifo chooser at 00547117)
	//   c JoinMatch(id,team,pw) -> s YouJoinedMatch to the joiner, PlayerJoined to everyone already in
	//   c SetSide(side)       -> s PlayerChangedSide to the match
	//   c StartMatch()        -> s CantStartMatchNotAllPlayersHaveSide(sideless) to the leader, or
	//                            StartClicked(teleportInSeconds) to every member
	//   c TeleportToMinigame() -> the world tier change-maps the member into the match's map, where the entry
	//                            routes to the MATCH-OWNED instance (WorldState.ResolveEntryInstance, #268/#512)
	//
	// Sides are the client's own values, stored and re-broadcast, never interpreted: the GUI produces them and the
	// GUI consumes them, and nothing server-side needs to know which number is which team yet (scoring is #146's
	// next slice). "Has a side" means the member has picked one at least once.
	public sealed class PvpMatchService(ILogger logger, TeamService teams, Func<uint, INetworkClient?> clientForCharacter, IGameServerDispatcher gameServer)
	{
		// A member's side before they pick one. The client's own GUI starts a fresh member sideless (the
		// CantStartMatch refusal exists exactly because of that state); 0 is what PlayerJoined announces for one.
		private const uint NoSide = 0;

		// StartClicked's countdown. ⚠️ An ASSUMPTION (nothing captured carries the value retail used): long enough
		// to read the announcement, short enough to not stall the match. Noted on #146.
		private const uint TeleportInSeconds = 10;

		private readonly ConcurrentDictionary<uint, LiveMatch> _matches = [];
		private readonly ConcurrentDictionary<uint, uint> _memberships = []; // characterId -> matchId
		private uint _nextMatchId;

		/// <summary>c2s 0x204A: create a custom match from the client-built ruleset. The creator is its leader.</summary>
		public async Task HandleCreateMatch(INetworkClient client, PvpMatchRules rules)
		{
			var characterId = client.Session.Character.Id;
			if (_memberships.ContainsKey(characterId))
			{
				// No refusal message is recovered for this (the ErrorCode values are unnamed), so refuse silently
				// server-side rather than invent a code the client may render as anything.
				logger.LogInformation("PvP match: {CharacterId:X8} tried to create a match while already in one - ignored.", characterId);
				return;
			}

			var match = new LiveMatch(Interlocked.Increment(ref _nextMatchId), rules, characterId);
			match.Sides[characterId] = null;
			_matches[match.Id] = match;
			_memberships[characterId] = match.Id;

			logger.LogInformation("PvP match {MatchId}: created by {CharacterId:X8} - map {MapId}, type {Type}, {Teams} team(s) of {Min}..{Max}.", match.Id, characterId, rules.MapId,
				rules.MatchType, rules.NumberOfTeams, rules.TeamSizeMin, rules.TeamSizeMax);

			await Push(characterId, CSPlayerInterfaceMethods.DispatchPvPMatchMakingYouJoinedMatch, PvpMatchCodec.Build(Payload(match)));
		}

		/// <summary>c2s 0x204B: join an existing match, optionally bringing the whole team.</summary>
		public async Task HandleJoinMatch(INetworkClient client, uint matchId, bool bringTeam, string? password)
		{
			var characterId = client.Session.Character.Id;
			if (!_matches.TryGetValue(matchId, out var match))
			{
				logger.LogInformation("PvP match: {CharacterId:X8} tried to join match {MatchId}, which does not exist.", characterId, matchId);
				return;
			}

			if (match.Banned.ContainsKey(characterId))
			{
				logger.LogInformation("PvP match {MatchId}: {CharacterId:X8} is banned from it - join refused.", matchId, characterId);
				return;
			}

			if (match.Rules.HasPassword && !string.Equals(match.Rules.Password, password, StringComparison.Ordinal))
			{
				logger.LogInformation("PvP match {MatchId}: {CharacterId:X8} gave a wrong password - join refused.", matchId, characterId);
				return;
			}

			var joiners = new List<uint> { characterId };
			if (bringTeam && teams.TeamOf(characterId) is { } teamId)
				joiners.AddRange(teams.MembersOf(teamId).Where(m => m != characterId));

			foreach (var joiner in joiners)
				await AddMember(match, joiner);
		}

		/// <summary>c2s 0x204C: the member picks a side. Stored and re-broadcast; the values are the client's own.</summary>
		public async Task HandleSetSide(INetworkClient client, uint side)
		{
			var characterId = client.Session.Character.Id;
			if (MatchOf(characterId) is not { } match)
				return;

			if (match.LockedSides.TryGetValue(characterId, out var locked))
			{
				await Push(characterId, CSPlayerInterfaceMethods.DispatchPvPMatchMakingCantChangeSideYouHaveBeenLockedToSide, U32(locked));
				return;
			}

			match.Sides[characterId] = side;
			await Broadcast(match, CSPlayerInterfaceMethods.DispatchPvPMatchMakingPlayerChangedSide, Id32AndU32(characterId, side));
		}

		/// <summary>c2s 0x204D: match chat. Relayed to every member with the sender's name.</summary>
		public Task HandleMatchSay(INetworkClient client, string message)
		{
			if (MatchOf(client.Session.Character.Id) is not { } match)
				return Task.CompletedTask;

			using var s = new FaolanStream();
			s.WriteString(client.Session.Character.Name ?? "").WriteString(message).WriteByte(false);
			return Broadcast(match, CSPlayerInterfaceMethods.DispatchPvPMatchMakingMatchSay, s.ToArray());
		}

		/// <summary>c2s 0x204E: leave. The match dissolves when it empties; the lead moves when the leader goes.</summary>
		public Task HandleLeaveMatch(INetworkClient client)
		{
			return RemoveMember(client.Session.Character.Id, kickedBy: null, banned: false, message: "");
		}

		/// <summary>c2s 0x2050: the match-info poll (the buildup view's refresh).</summary>
		public async Task HandleGetMatchInfo(INetworkClient client, uint matchId)
		{
			if (_matches.TryGetValue(matchId, out var match))
				await Push(client.Session.Character.Id, CSPlayerInterfaceMethods.DispatchPvPMatchMakingMatchInfo, PvpMatchCodec.Build(Payload(match)));
		}

		/// <summary>c2s 0x2056 / 0x2057: the leader removes (and with Ban blocks) a member.</summary>
		public Task HandleKickPlayer(INetworkClient client, uint targetId, string message, bool ban)
		{
			var characterId = client.Session.Character.Id;
			if (MatchOf(characterId) is not { } match || match.LeaderId != characterId || targetId == characterId)
				return Task.CompletedTask;

			return RemoveMember(targetId, kickedBy: characterId, banned: ban, message);
		}

		/// <summary>c2s 0x2058: the leader hands the match to another member.</summary>
		public async Task HandleMakePlayerMatchOwner(INetworkClient client, uint targetId)
		{
			var characterId = client.Session.Character.Id;
			if (MatchOf(characterId) is not { } match || match.LeaderId != characterId || !match.Sides.ContainsKey(targetId))
				return;

			match.LeaderId = targetId;
			await Broadcast(match, CSPlayerInterfaceMethods.DispatchPvPMatchMakingNewMatchLeader, Id32(targetId));
		}

		/// <summary>c2s 0x2059: the leader forces (and locks) a member's side.</summary>
		public async Task HandleForceSide(INetworkClient client, uint targetId, uint side)
		{
			var characterId = client.Session.Character.Id;
			if (MatchOf(characterId) is not { } match || match.LeaderId != characterId || !match.Sides.ContainsKey(targetId))
				return;

			match.Sides[targetId] = side;
			match.LockedSides[targetId] = side;
			await Broadcast(match, CSPlayerInterfaceMethods.DispatchPvPMatchMakingPlayerChangedSide, Id32AndU32(targetId, side));
			await Broadcast(match, CSPlayerInterfaceMethods.DispatchPvPMatchMakingCharacterNowLockedToSide, Id32AndU32(targetId, side));
		}

		/// <summary>c2s 0x205A: the leader starts the match. Refused while any member is sideless.</summary>
		public async Task HandleStartMatch(INetworkClient client)
		{
			var characterId = client.Session.Character.Id;
			if (MatchOf(characterId) is not { } match || match.LeaderId != characterId || match.Started)
				return;

			var sideless = match.Sides.Where(kv => kv.Value == null).Select(kv => kv.Key).ToList();
			if (sideless.Count > 0)
			{
				// i_cPlayesWithoutSide [sic] - a ListFrame of ID32 pairs (the reader FUN_007a4df0 walks 8-byte
				// elements of two u32 reads).
				using var s = new FaolanStream();
				s.WriteUInt32(ListFrame.ForCount(sideless.Count));
				foreach (var id in sideless)
					s.WriteUInt32(EntityClass.Character).WriteUInt32(id);

				await Push(characterId, CSPlayerInterfaceMethods.DispatchPvPMatchMakingCantStartMatchNotAllPlayersHaveSide, s.ToArray());
				return;
			}

			match.Started = true;
			match.StartedAtUtc = DateTime.UtcNow;
			logger.LogInformation("PvP match {MatchId}: started by {CharacterId:X8} with {Members} member(s); teleport in {Seconds}s.", match.Id, characterId, match.Sides.Count,
				TeleportInSeconds);
			await Broadcast(match, CSPlayerInterfaceMethods.DispatchPvPMatchMakingStartClicked, U32(TeleportInSeconds));
		}

		/// <summary>
		///     c2s 0x2051: the member's client asks to be moved into the minigame (it sends this itself after
		///     StartClicked's countdown). The world tier runs the ordinary change-map handoff; the entry then routes
		///     to the match-owned instance via <see cref="PvpMatchEntryOf" />.
		/// </summary>
		public async Task HandleTeleportToMinigame(INetworkClient client)
		{
			var characterId = client.Session.Character.Id;
			if (MatchOf(characterId) is not { } match || !match.Started)
			{
				logger.LogInformation("PvP match: {CharacterId:X8} asked to teleport to a minigame while in no started match.", characterId);
				return;
			}

			if (!await gameServer.TeleportCharacter(characterId, match.Rules.MapId))
				logger.LogWarning("PvP match {MatchId}: the world tier could not teleport {CharacterId:X8} to map {MapId}.", match.Id, characterId, match.Rules.MapId);
		}

		/// <summary>
		///     The started match whose minigame map is <paramref name="mapId" /> and holds <paramref name="characterId" /> -
		///     what the world entry asks to route the member into the MATCH-OWNED playfield instance (the minigame's twin
		///     of #512's team instance) and to tell the client the match clock.
		/// </summary>
		public PvpMatchWorldEntry? PvpMatchEntryOf(uint characterId, uint mapId)
		{
			if (MatchOf(characterId) is not { } match || !match.Started || match.Rules.MapId != mapId)
				return null;

			return new PvpMatchWorldEntry(match.Id, (uint)Math.Max(0, (DateTime.UtcNow - match.StartedAtUtc).TotalSeconds));
		}

		/// <summary>The disconnect hook: a member's connection going away is a leave.</summary>
		public void Forget(uint characterId)
		{
			if (_memberships.ContainsKey(characterId))
				_ = RemoveMember(characterId, kickedBy: null, banned: false, message: "");
		}

		private async Task AddMember(LiveMatch match, uint characterId)
		{
			if (_memberships.ContainsKey(characterId) || !match.Sides.TryAdd(characterId, null))
				return;

			_memberships[characterId] = match.Id;

			// Everyone already in hears the join first, then the joiner gets the whole match - so the payload the
			// joiner receives already lists them, and the broadcast does not name them to themselves twice.
			foreach (var member in match.Sides.Keys.Where(m => m != characterId))
				await Push(member, CSPlayerInterfaceMethods.DispatchPvPMatchMakingPlayerJoined, Id32AndU32(characterId, NoSide));

			await Push(characterId, CSPlayerInterfaceMethods.DispatchPvPMatchMakingYouJoinedMatch, PvpMatchCodec.Build(Payload(match)));
			logger.LogInformation("PvP match {MatchId}: {CharacterId:X8} joined ({Members} member(s)).", match.Id, characterId, match.Sides.Count);
		}

		private async Task RemoveMember(uint characterId, uint? kickedBy, bool banned, string message)
		{
			if (MatchOf(characterId) is not { } match || !match.Sides.TryRemove(characterId, out _))
				return;

			_memberships.TryRemove(characterId, out _);
			match.LockedSides.TryRemove(characterId, out _);
			if (banned)
				match.Banned.TryAdd(characterId, 0);

			if (kickedBy != null)
			{
				// The kicked player hears it too - that message is how their client learns it is out of the match.
				using var s = new FaolanStream();
				s.WriteUInt32(EntityClass.Character).WriteUInt32(characterId).WriteString(message);
				var body = s.ToArray();
				await Push(characterId, CSPlayerInterfaceMethods.DispatchPvPMatchMakingPlayerKicked, body);
				await Broadcast(match, CSPlayerInterfaceMethods.DispatchPvPMatchMakingPlayerKicked, body);
			}
			else
			{
				await Broadcast(match, CSPlayerInterfaceMethods.DispatchPvPMatchMakingPlayerLeft, Id32(characterId));
			}

			if (match.Sides.IsEmpty)
			{
				_matches.TryRemove(match.Id, out _);
				logger.LogInformation("PvP match {MatchId}: dissolved (last member left).", match.Id);
				return;
			}

			if (match.LeaderId == characterId)
			{
				// The lead passes to the longest-standing remaining member. ⚠️ An ASSUMPTION - nothing recovered
				// states retail's succession rule - but some member must lead, and the client renders whoever
				// NewMatchLeader names. Noted on #146.
				match.LeaderId = match.Sides.Keys.First();
				await Broadcast(match, CSPlayerInterfaceMethods.DispatchPvPMatchMakingNewMatchLeader, Id32(match.LeaderId));
			}
		}

		private LiveMatch? MatchOf(uint characterId)
		{
			return _memberships.TryGetValue(characterId, out var matchId) && _matches.TryGetValue(matchId, out var match)
				? match
				: null;
		}

		private PvpMatch Payload(LiveMatch match)
		{
			// The roster is the live members' team-member records - the SAME cCharacter record the team subsystem
			// carries (the client's match reader calls the identical FUN_005574d0). A member whose record cannot be
			// resolved (no live session) is left off rather than sent as an empty shell.
			var roster = match.Sides.Keys
				.Select(teams.MemberRecordOf)
				.OfType<TeamMemberRecord>()
				.ToList();

			return new PvpMatch(roster, [], match.Rules, match.Id) { LeaderId = match.LeaderId };
		}

		private Task Broadcast(LiveMatch match, CSPlayerInterfaceMethods method, byte[] body)
		{
			return Task.WhenAll(match.Sides.Keys.Select(member => Push(member, method, body)));
		}

		private Task Push(uint characterId, CSPlayerInterfaceMethods method, byte[] body)
		{
			return clientForCharacter(characterId) is { } client
				? TeamService.SendToClient(client, method, body)
				: Task.CompletedTask;
		}

		private static byte[] U32(uint value)
		{
			using var s = new FaolanStream();
			s.WriteUInt32(value);
			return s.ToArray();
		}

		private static byte[] Id32(uint characterId)
		{
			using var s = new FaolanStream();
			s.WriteUInt32(EntityClass.Character).WriteUInt32(characterId);
			return s.ToArray();
		}

		private static byte[] Id32AndU32(uint characterId, uint value)
		{
			using var s = new FaolanStream();
			s.WriteUInt32(EntityClass.Character).WriteUInt32(characterId).WriteUInt32(value);
			return s.ToArray();
		}

		// One live custom match. Sides: characterId -> the side they picked (null until they pick one).
		private sealed class LiveMatch(uint id, PvpMatchRules rules, uint leaderId)
		{
			public uint Id { get; } = id;
			public PvpMatchRules Rules { get; } = rules;
			public uint LeaderId { get; set; } = leaderId;
			public bool Started { get; set; }
			public DateTime StartedAtUtc { get; set; }
			public ConcurrentDictionary<uint, uint?> Sides { get; } = [];
			public ConcurrentDictionary<uint, uint> LockedSides { get; } = [];
			public ConcurrentDictionary<uint, byte> Banned { get; } = [];
		}
	}
}
