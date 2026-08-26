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

		/// <summary>
		///     The queue's match-formation pass (#146), run after each new signup: a match forms when the resolved
		///     minigame's queue is FULL - the capacities the client's own window displays (24 for Conquest, 12 for
		///     the rest; StartingData.MinigameQueueCapacity) - with sides locked alternating in queue order.
		///     Under capacity nothing pops; the GM `.matchstart` is the force-with-fewer override.
		/// </summary>
		public async Task TryFormFromQueue(IReadOnlyList<(uint CharacterId, PvpMatchMakingSignup Signup)> queued, Action<uint> dequeue)
		{
			var outcome = await FormFromQueue(queued, dequeue, force: false);
			logger.LogDebug("PvP matchmaking formation pass: {Outcome}", outcome);
		}

		/// <summary>
		///     The GM `.matchstart` override: form a match from whoever is compatibly queued RIGHT NOW, however few -
		///     down to a single member, which is what lets one client walk the teleport + match-instance flow alone.
		///     Returns what happened, for the GM's chat line.
		/// </summary>
		public Task<string> ForceFormFromQueue(IReadOnlyList<(uint CharacterId, PvpMatchMakingSignup Signup)> queued, Action<uint> dequeue)
		{
			return FormFromQueue(queued, dequeue, force: true);
		}

		private async Task<string> FormFromQueue(IReadOnlyList<(uint CharacterId, PvpMatchMakingSignup Signup)> queued, Action<uint> dequeue, bool force)
		{
			// Whoever is already in a match keeps their signup out of the pairing (stale rows are dequeued).
			var eligible = queued.Where(q => !_memberships.ContainsKey(q.CharacterId)).ToList();
			if (eligible.Count < 1)
				return "nothing formed - the queue holds no eligible signup.";

			// The anchor is the longest-queued signup; everyone compatible with it joins the match. A concrete
			// signup among them names the minigame; all "join first available" falls back to the pair the captured
			// signups used most - Conquest (type 4) on The Call of Jhebbal Sag (5006).
			var anchor = eligible[0];
			var members = eligible.Where(q => Compatible(anchor.Signup, q.Signup)).ToList();

			var pick = members.FirstOrDefault(m => !m.Signup.IsAnyAvailable).Signup ?? anchor.Signup;
			var (mapId, matchType, queueType) = pick.IsAnyAvailable
				? (5006u, 4u, 0u)
				: (pick.MapId, pick.MinigameType, pick.QueueType);

			// The match forms when the minigame's queue is FULL - the per-map capacity the client's own window
			// shows (0/24, 0/12). `.matchstart` overrides with whatever is there; either way no match takes more
			// than the capacity.
			var capacity = StartingData.MinigameQueueCapacity.GetValueOrDefault(mapId, StartingData.DefaultMinigameQueueCapacity);
			if (!force && members.Count < capacity)
				return $"queue at {members.Count}/{capacity} for map {mapId} - forming at capacity (`.matchstart` forces).";

			if (members.Count > capacity)
				members = members.Take(capacity).ToList();

			// The ruleset states THIS match truthfully: two sides of however many the queue held. Time limit 900 s
			// is the client's own debug default; the score limit is a placeholder the client only displays.
			// Non-silent, so every client announces the join with its own fifo text.
			var perSide = (uint)Math.Max(1, (members.Count + 1) / 2);
			var rules = new PvpMatchRules(matchType, mapId, NumberOfTeams: 2, TeamSizeMin: 1, TeamSizeMax: perSide, Password: "", TimeLimitSeconds: 900, ScoreLimit: 100,
				FieldH: 0, HasPassword: false, Silent: false, QueueType: queueType);

			var match = new LiveMatch(Interlocked.Increment(ref _nextMatchId), rules, anchor.CharacterId) { Started = true, StartedAtUtc = DateTime.UtcNow };
			for (var i = 0; i < members.Count; i++)
			{
				var side = (uint)(i % 2) + 1; // alternate 1/2 in queue order
				var id = members[i].CharacterId;
				match.Sides[id] = side;
				match.LockedSides[id] = side;
				_memberships[id] = match.Id;
				dequeue(id);
			}

			_matches[match.Id] = match;

			logger.LogInformation("PvP match {MatchId}: FORMED from the queue - {Members} member(s) on map {MapId} (type {Type}); teleport in {Seconds}s.",
				match.Id, members.Count, mapId, matchType, TeleportInSeconds);

			var payload = PvpMatchCodec.Build(Payload(match));
			foreach (var member in members)
				await Push(member.CharacterId, CSPlayerInterfaceMethods.DispatchPvPMatchMakingYouJoinedMatch, payload);

			// Announce every member to every member as a PlayerJoined - [cCharacter record][u32 side], the reader's
			// own shape. The client DEDUPS against its roster by id (FUN_005471b0 walks the 0x7c-stride vector
			// before appending), so a member already listed just drives the side/refresh path - and that refresh is
			// what fills the buildup view, which stayed blank when the roster arrived only inside i_cMatch.
			foreach (var member in members)
				if (PlayerJoinedBody(member.CharacterId, match) is { } joined)
					await Broadcast(match, CSPlayerInterfaceMethods.DispatchPvPMatchMakingPlayerJoined, joined);

			// A matchmade match's sides are the queue's, not the player's pick - the lock messages are how the
			// client learns both facts.
			foreach (var member in members)
				await Broadcast(match, CSPlayerInterfaceMethods.DispatchPvPMatchMakingCharacterNowLockedToSide, Id32AndU32(member.CharacterId, match.Sides[member.CharacterId]!.Value));

			await Broadcast(match, CSPlayerInterfaceMethods.DispatchPvPMatchMakingStartClicked, U32(TeleportInSeconds));
			ScheduleMatchTeleport(match);
			return $"match {match.Id} formed: {members.Count} member(s) -> map {mapId} (type {matchType}), teleport in {TeleportInSeconds}s.";
		}

		// Two signups can share a match when they name the same minigame - or when either took "join first
		// available", which is compatible with anything.
		private static bool Compatible(PvpMatchMakingSignup a, PvpMatchMakingSignup b)
		{
			return a.IsAnyAvailable || b.IsAnyAvailable || (a.MapId == b.MapId && a.MinigameType == b.MinigameType && a.QueueType == b.QueueType);
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

		/// <summary>c2s 0x204E: leave (the buildup view's Cancel Match). The match dissolves when it empties; the
		/// lead moves when the leader goes. A leaver in NO known match still gets MatchRemoved - that is the stale-
		/// state recovery: a client that believed itself in a match this process never knew (a server restart, seen
		/// live 2026-08-26) presses Cancel and its window resets instead of sticking on the buildup page.</summary>
		public async Task HandleLeaveMatch(INetworkClient client)
		{
			var characterId = client.Session.Character.Id;
			if (MatchOf(characterId) == null)
			{
				await Push(characterId, CSPlayerInterfaceMethods.DispatchPvPMatchMakingMatchRemoved, []);
				return;
			}

			await RemoveMember(characterId, kickedBy: null, banned: false, message: "");
		}

		/// <summary>c2s 0x2050: the match-info poll (the buildup view's refresh). Asking about a match this process
		/// does not hold answers MatchRemoved - the poller is stale (a pre-restart match) and resets itself.</summary>
		public async Task HandleGetMatchInfo(INetworkClient client, uint matchId)
		{
			if (_matches.TryGetValue(matchId, out var match))
				await Push(client.Session.Character.Id, CSPlayerInterfaceMethods.DispatchPvPMatchMakingMatchInfo, PvpMatchCodec.Build(Payload(match)));
			else
				await Push(client.Session.Character.Id, CSPlayerInterfaceMethods.DispatchPvPMatchMakingMatchRemoved, []);
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

		/// <summary>c2s 0x205A: the leader starts the match. Refused while any member is sideless. Pressed from a
		/// match this process does not hold (stale pre-restart state, seen live 2026-08-26: the buildup view
		/// survives a relog and its Start button sends this), the answer is MatchRemoved so the window resets.</summary>
		public async Task HandleStartMatch(INetworkClient client)
		{
			var characterId = client.Session.Character.Id;
			if (MatchOf(characterId) == null)
			{
				await Push(characterId, CSPlayerInterfaceMethods.DispatchPvPMatchMakingMatchRemoved, []);
				return;
			}

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
			ScheduleMatchTeleport(match);
		}

		// ⭐ Verified live (2026-08-26): the client does NOT ask to be moved after StartClicked's countdown - its
		// own fifo says "Entering minigame in about 10 seconds", passive - so the SERVER initiates the move for
		// every member when the countdown lapses. The c2s TeleportToMinigame below is the manual path (the PvP
		// window's own teleport/rejoin button), kept for a member who missed the move or re-enters.
		private void ScheduleMatchTeleport(LiveMatch match)
		{
			_ = Task.Run(async () =>
			{
				try
				{
					await Task.Delay(TimeSpan.FromSeconds(TeleportInSeconds));
					foreach (var member in match.Sides.Keys)
					{
						// Where to put them back when they leave: their live transform the moment the match takes
						// them, so LeaveMatch is a round trip and not a one-way move.
						if (gameServer.GetCharacterLiveState(member) is { } from && from.MapId != match.Rules.MapId)
							match.ReturnTo[member] = (from.MapId, new System.Numerics.Vector3(from.X, from.Y, from.Z));

						if (!await gameServer.TeleportCharacter(member, match.Rules.MapId))
							logger.LogWarning("PvP match {MatchId}: the world tier could not move {CharacterId:X8} to map {MapId}.", match.Id, member, match.Rules.MapId);
					}
				}
				catch (Exception e)
				{
					logger.LogError(e, "PvP match {MatchId}: the scheduled minigame teleport failed.", match.Id);
				}
			});
		}

		/// <summary>
		///     c2s 0x2051: the manual teleport/rejoin into the member's started minigame (the countdown's own move is
		///     server-initiated, above). The world tier runs the ordinary change-map handoff; the entry then routes
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
			// ⚠️ PlayerJoined carries the WHOLE cCharacter record plus the side, not a bare ID32 - its dispatch
			// reader (FUN_01346780) calls the same FUN_005574d0 the roster uses. A 12-byte ID32 form is silently
			// discarded as stream-bad, which is how the buildup view stayed empty on the first live run.
			if (PlayerJoinedBody(characterId, match) is { } joined)
				foreach (var member in match.Sides.Keys.Where(m => m != characterId))
					await Push(member, CSPlayerInterfaceMethods.DispatchPvPMatchMakingPlayerJoined, joined);

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

			// PlayerLeft/PlayerKicked carry the WHOLE cCharacter record (their readers call the same FUN_005574d0
			// the roster uses; Kicked appends the message string) - a bare ID32 is discarded as stream-bad.
			var record = teams.MemberRecordOf(characterId);
			if (kickedBy != null)
			{
				using var s = new FaolanStream();
				if (record != null)
				{
					var rec = TeamMemberRecordCodec.Build(record);
					s.Write(rec, 0, rec.Length);
				}

				s.WriteString(message);
				var body = s.ToArray();
				// The kicked player hears it too - that message is how their client learns it is out of the match.
				if (record != null)
				{
					await Push(characterId, CSPlayerInterfaceMethods.DispatchPvPMatchMakingPlayerKicked, body);
					await Broadcast(match, CSPlayerInterfaceMethods.DispatchPvPMatchMakingPlayerKicked, body);
				}
			}
			else if (record != null)
			{
				await Broadcast(match, CSPlayerInterfaceMethods.DispatchPvPMatchMakingPlayerLeft, TeamMemberRecordCodec.Build(record));
			}

			// Leaving the match while standing IN its minigame returns the member to where they were before the
			// countdown teleport took them (recorded at teleport time). Leaving from anywhere else moves nobody.
			if (match.ReturnTo.TryRemove(characterId, out var back)
			    && gameServer.GetCharacterLiveState(characterId) is { } live && live.MapId == match.Rules.MapId)
			{
				if (!await gameServer.TeleportCharacter(characterId, back.MapId, back.Position))
					logger.LogWarning("PvP match {MatchId}: could not return {CharacterId:X8} to map {MapId}.", match.Id, characterId, back.MapId);
			}

			// The removed member's own window resets on MatchRemoved (0x207C - the no-arg feedback override the
			// client's log string names outright); PlayerLeft/PlayerKicked only tell the OTHERS.
			await Push(characterId, CSPlayerInterfaceMethods.DispatchPvPMatchMakingMatchRemoved, []);

			if (match.Sides.IsEmpty)
			{
				_matches.TryRemove(match.Id, out _);
				logger.LogInformation("PvP match {MatchId}: dissolved (last member left).", match.Id);
				return;
			}

			if (match.LeaderId == characterId)
			{
				// The buildup view's "Cancel Match" IS a leader leave - there is no separate cancel method on the
				// wire - so a leader leaving a match that has NOT started cancels it for everyone: MatchRemoved to
				// every remaining member, match gone. A leader leaving a STARTED match instead passes the lead
				// (NewMatchLeader exists for exactly that). ⚠️ The started/unstarted split is an ASSUMPTION -
				// nothing recovered states retail's rule - noted on #146.
				if (!match.Started)
				{
					await Broadcast(match, CSPlayerInterfaceMethods.DispatchPvPMatchMakingMatchRemoved, []);
					foreach (var member in match.Sides.Keys)
					{
						match.Sides.TryRemove(member, out _);
						_memberships.TryRemove(member, out _);
					}

					_matches.TryRemove(match.Id, out _);
					logger.LogInformation("PvP match {MatchId}: CANCELLED by its leader before start; every member released.", match.Id);
					return;
				}

				// The lead passes to the longest-standing remaining member. ⚠️ An ASSUMPTION too, same note.
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

		// [cCharacter record][u32 side] - DispatchPvPMatchMakingPlayerJoined's body per its own reader. Null when
		// the member's record cannot be resolved (no live session), in which case nothing is sent.
		private byte[]? PlayerJoinedBody(uint characterId, LiveMatch match)
		{
			if (teams.MemberRecordOf(characterId) is not { } record)
				return null;

			using var s = new FaolanStream();
			var rec = TeamMemberRecordCodec.Build(record);
			s.Write(rec, 0, rec.Length);
			s.WriteUInt32(match.Sides.TryGetValue(characterId, out var side) && side is { } v ? v : NoSide);
			return s.ToArray();
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

			// Each member's pre-match transform, recorded when the countdown teleport takes them - what LeaveMatch
			// restores. Absent for a member who was already on the minigame map when the match formed.
			public ConcurrentDictionary<uint, (uint MapId, System.Numerics.Vector3 Position)> ReturnTo { get; } = [];
		}
	}
}
