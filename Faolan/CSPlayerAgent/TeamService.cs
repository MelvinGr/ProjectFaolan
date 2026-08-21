using Faolan.Core.Constants;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.GroupFinder;
using Faolan.Core.Network.Codecs.Team;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Network.Server.ChatServer;
using Faolan.Core.Network.Server.GameServer;
using Faolan.Core.Protobuf;
using Microsoft.Extensions.Logging;

namespace Faolan.CSPlayerAgent
{
	// Teams (groups): the live roster plus the wire fan-out. Decoded in docs/subsystems/teams-trade-friends.md from
	// login_2-clients_group-trade-friend; every message this builds is gated byte-exact by TeamTradeFriendCodecTests.
	//
	// A team is EPHEMERAL - nothing here is persisted, and that is deliberate: retail teams do not survive a logout,
	// so a DB table would be state with no owner. That also means no migration and no seed data.
	//
	// A composed collaborator of the CsPlayerAgent listener, exactly like GuildService: the CS tier owns the roster and
	// every roster dispatch, and takes the cross-connection client lookup (ClientForCharacter) as a delegate seam. The
	// other two tiers reach it the way every cross-listener action does, through a dispatcher: the world listener calls
	// ICsPlayerAgentDispatcher for the LeaveTeam/KickFromTeam that arrive on the GAME connection, and the roster's own
	// world-side pushes go back out through IGameServerDispatcher, as the guild tag push does.
	public sealed class TeamService
	{
		// Retail's team instances are minted sequentially by its CS tier (the capture's three groupings are
		// 0x0051DD9A, 0x0051DD9B, 0x0051DD9C). Ours count from a base in the same shape; the value is opaque to the
		// client, only its identity matters.
		private const uint FirstTeamId = 0x00500000;

		// The SENDER of every CsPlayerAgent s2c packet: the guild/team server object. Instance 0x04 is what retail
		// stamps (0x04 on the two-client capture and the Hoxxling login, 0x52 on one create session - an arbitrary
		// server handle, not session data).
		private static readonly ProtoEndpoint PushSender = Endpoints.Endpoint(RpcInterfaces.CSPlayerAgentType, 0x04);

		private readonly ConcurrentDictionary<uint, uint> _teamByCharacter = [];

		// invitee -> inviter, for the window between TeamInvite and AcceptTeamInvite.
		private readonly ConcurrentDictionary<uint, uint> _pendingInvites = [];
		private readonly ConcurrentDictionary<uint, LiveTeam> _teams = [];
		private readonly IChatServerDispatcher _chatServerDispatcher;
		private readonly IGameServerDispatcher _gameServerDispatcher;
		private readonly Func<uint, INetworkClient?> _csClientForCharacter;
		private readonly ILogger _logger;
		private uint _nextTeamId = FirstTeamId;

		public TeamService(ILogger logger, IGameServerDispatcher gameServerDispatcher, IChatServerDispatcher chatServerDispatcher, Func<uint, INetworkClient?> clientForCharacter)
		{
			_logger = logger;
			_gameServerDispatcher = gameServerDispatcher;
			_chatServerDispatcher = chatServerDispatcher;
			_csClientForCharacter = clientForCharacter;
		}

		// A team member is ONLINE by definition, so its row comes from the live session rather than the DB.
		private Character? CharacterOf(uint characterId)
		{
			return _csClientForCharacter(characterId)?.Session.Character;
		}

		// The member record, stamped with the member's LIVE playfield from the world listener rather than the
		// Character.MapId column, which only updates at save points - see TeamMemberRecord.FromCharacter.
		private TeamMemberRecord MemberOf(Character character)
		{
			var live = _gameServerDispatcher.GetCharacterLiveState(character.Id);
			return TeamMemberRecord.FromCharacter(character, live?.MapId ?? character.MapId, live?.PlayfieldToken);
		}

		// 0x2009 DeclineTeamInvite: forget the pending invite. Retail puts nothing on the wire for it.
		public void Decline(uint characterId)
		{
			_pendingInvites.TryRemove(characterId, out _);
		}

		// The team a character is in, or null. Used by the chat tier for the ~Team channel and by loot rules.
		public uint? TeamOf(uint characterId)
		{
			return _teamByCharacter.TryGetValue(characterId, out var teamId)
				? teamId
				: null;
		}

		public IReadOnlyList<uint> MembersOf(uint teamId)
		{
			return _teams.TryGetValue(teamId, out var team)
				? team.Members
				: [];
		}

		// One online character as the wire's `cCharacter`, independent of any team - the raid messages that name a
		// single person (DispatchRaidInvite, DispatchRaidTargetDeclinedRaidInvite, ...) carry this record for people
		// who are not in a team yet, so it cannot be reached through a roster.
		public TeamMemberRecord? MemberRecordOf(uint characterId)
		{
			return CharacterOf(characterId) is { } character
				? MemberOf(character)
				: null;
		}

		public uint? LeaderOf(uint teamId)
		{
			return _teams.TryGetValue(teamId, out var team)
				? team.LeaderId
				: null;
		}

		// The team a character is in, creating a one-person one if they are solo. A raid is built out of TEAMS, so a
		// player who forms or joins one without a group needs a team to be in first (#669); a one-member team is not
		// a state the team flow itself can reach, because Remove dissolves at one.
		public uint EnsureTeam(uint characterId)
		{
			return TeamFor(characterId).Id;
		}

		// The group finder forms the team it is about to hand an instance to (#511/#512): a solo queuer gets a
		// one-member "private team" WITH the full join fan-out - retail's DispatchYouJoinedTeam here carries a
		// one-record roster (chat-annotated-4 frame 11943), then AddToGroupIIR and the ~Team subscription, exactly
		// the shape a two-person accept produces. A queuer already grouped keeps their team; the roster re-broadcast
		// is what retail's re-entry does anyway and the pushes are idempotent.
		public async Task<uint> FormGroupFinderTeam(uint characterId)
		{
			var team = TeamFor(characterId);
			await BroadcastRoster(team);
			await PushMembership(team, characterId, true);
			foreach (var member in team.Members)
				await _chatServerDispatcher.RefreshTeamChannel(member, team.Id);

			_logger.LogInformation("Team 0x{Team:X8}: formed by the group finder for {CharacterId:X8} ({Count} member(s)).", team.Id, characterId, team.Members.Count);
			return team.Id;
		}

		// The team as the wire sees it - the client's `cTeam`, leader first. Null for a team that no longer exists or
		// whose members are all offline. The raid service composes these; the team flow reaches the same record
		// through BroadcastRoster.
		public TeamRoster? RosterOf(uint teamId)
		{
			if (!_teams.TryGetValue(teamId, out var team))
				return null;

			var records = team.Members.OrderByDescending(id => id == team.LeaderId).Select(CharacterOf).OfType<Character>().Select(MemberOf).ToList();

			return records.Count == 0
				? null
				: new TeamRoster(team.Id, team.LeaderId, records, team.Settings);
		}

		// Move a character between two teams that already exist - the raid's own MoveTeamMember/SwapCharacters, which
		// have no team-tier counterpart because a team is never a member of anything. Deliberately does NOT dissolve
		// the source team at one member the way Remove does: inside a raid a one-person team is a legitimate slot.
		public bool MoveMember(uint characterId, uint destinationTeamId)
		{
			if (TeamOf(characterId) is not { } sourceId || !_teams.TryGetValue(sourceId, out var source) || !_teams.TryGetValue(destinationTeamId, out var destination))
				return false;

			if (sourceId == destinationTeamId)
				return false;

			source.Remove(characterId);
			destination.Add(characterId);
			_teamByCharacter[characterId] = destinationTeamId;

			if (source.LeaderId == characterId && source.Members.Count > 0)
				source.LeaderId = source.Members[0];

			return true;
		}

		// c2s TeamInvite (0x2003): remember the pending invite and tell both sides. Which dispatch goes WHERE is worth
		// stating, because the names read backwards: retail sends DispatchYouInvitedToTeam to the INVITER carrying the
		// TARGET's record ("you invited X" - f674 -> f678, both on the inviter's connection), and DispatchTeamInvite to
		// the TARGET carrying the INVITER's ("X invites you" - f680, on the target's). Each side is shown the other.
		public async Task Invite(INetworkClient inviterClient, Character inviter, Character target, string message = "")
		{
			if (_teamByCharacter.ContainsKey(target.Id))
			{
				_logger.LogInformation("TeamInvite: {Target} is already in a team; ignoring {Inviter}'s invite.", target.Name, inviter.Name);
				return;
			}

			_pendingInvites[target.Id] = inviter.Id;

			await Send(inviterClient, CSPlayerInterfaceMethods.DispatchYouInvitedToTeam, TeamMemberRecordCodec.Build(MemberOf(target)));

			await Push(target.Id, CSPlayerInterfaceMethods.DispatchTeamInvite, TeamInviteCodec.Build(MemberOf(inviter), message));
			_logger.LogInformation("Team: {Inviter} invited {Target}.", inviter.Name, target.Name);
		}

		// c2s AcceptTeamInvite (0x2008): the invitee joins the inviter's team, creating it if the inviter was solo.
		// Retail then sends the WHOLE roster to every member (DispatchYouJoinedTeam) plus a DispatchTeamJoin naming
		// the joiner, and pushes AddToGroupIIR onto every member's world connection.
		public async Task AcceptInvite(uint accepterId)
		{
			if (!_pendingInvites.TryRemove(accepterId, out var inviterId))
			{
				_logger.LogWarning("AcceptTeamInvite from {Character:X8} with no pending invite.", accepterId);
				return;
			}

			var accepter = CharacterOf(accepterId);
			var inviter = CharacterOf(inviterId);
			if (accepter == null || inviter == null)
				return;

			var team = TeamFor(inviterId);
			team.Add(accepterId);
			_teamByCharacter[accepterId] = team.Id;

			await BroadcastRoster(team);
			await Broadcast(team, CSPlayerInterfaceMethods.DispatchTeamJoin, TeamMemberEventCodec.Build(team.Id, MemberOf(accepter)));
			await PushMembership(team, accepterId, true);

			// The ~Team chat channel, to EVERY member and not just the joiner - retail subscribes both clients when the
			// team forms (frames 701/702), and the call is idempotent, so a member already on it is a no-op (#668).
			foreach (var member in team.Members)
				await _chatServerDispatcher.RefreshTeamChannel(member, team.Id);
			_logger.LogInformation("Team 0x{Team:X8}: {Character} joined ({Count} member(s), leader {Leader}).", team.Id, accepter.Name, team.Members.Count, inviter.Name);
		}

		// c2s LeaveTeam (0x2008 on the GAME tier - a different interface's method 0x2008 than AcceptTeamInvite).
		// Retail sends DispatchTeamLeave to the remaining members AND the leaver, hands the lead to someone else when
		// the leader leaves (DispatchSetNewTeamLeader), and pushes RemoveFromGroupIIR.
		public async Task<bool> Leave(uint characterId)
		{
			if (TeamOf(characterId) == null)
				return false;

			await Remove(characterId, CSPlayerInterfaceMethods.DispatchTeamLeave, "left");
			return true;
		}

		// c2s KickFromTeam (0x2009, GAME tier): only the leader may kick. The target gets
		// DispatchYouGotKickedFromTeam, everyone else the same DispatchTeamLeave a voluntary leave produces.
		public async Task<bool> Kick(uint leaderId, uint targetId)
		{
			if (TeamOf(leaderId) is not { } teamId || !_teams.TryGetValue(teamId, out var team) || team.LeaderId != leaderId)
			{
				_logger.LogWarning("KickFromTeam from {Character:X8} who is not a team leader.", leaderId);
				return false;
			}

			if (CharacterOf(targetId) is { } target)
				await Push(targetId, CSPlayerInterfaceMethods.DispatchYouGotKickedFromTeam, TeamMemberRecordCodec.Build(MemberOf(target)));

			await Remove(targetId, CSPlayerInterfaceMethods.DispatchTeamLeave, "was kicked");
			return true;
		}

		// A member leaving for any reason - voluntarily, kicked, or disconnected. Dissolves the team at one member,
		// which is what retail does: a one-person team is no team.
		private async Task Remove(uint characterId, CSPlayerInterfaceMethods opcode, string why)
		{
			if (TeamOf(characterId) is not { } teamId || !_teams.TryGetValue(teamId, out var team))
				return;

			var leaver = CharacterOf(characterId);
			team.Remove(characterId);
			_teamByCharacter.TryRemove(characterId, out _);

			if (leaver != null)
			{
				// The leaver is told too - retail sends DispatchTeamLeave to the whole former roster, not just those
				// remaining, so the leaver's own client closes its team window.
				var body = TeamMemberEventCodec.Build(team.Id, MemberOf(leaver));
				await Broadcast(team, opcode, body);
				await Push(characterId, opcode, body);
			}

			await PushMembership(team, characterId, false);
			await _gameServerDispatcher.SendGroupMembership(characterId, characterId, team.Id, 0, false);
			await _chatServerDispatcher.RefreshTeamChannel(characterId, null); // off the ~Team channel with the roster (#668)

			// The leader left: promote whoever is left and say so.
			if (team.LeaderId == characterId && team.Members.Count > 0)
			{
				team.LeaderId = team.Members[0];
				if (CharacterOf(team.LeaderId) is { } newLeader)
					await Broadcast(team, CSPlayerInterfaceMethods.DispatchSetNewTeamLeader, TeamMemberEventCodec.Build(team.Id, MemberOf(newLeader)));
			}

			_logger.LogInformation("Team 0x{Team:X8}: {Character} {Why} ({Count} member(s) left).", team.Id, leaver?.Name ?? $"0x{characterId:X8}", why, team.Members.Count);

			if (team.Members.Count <= 1)
				await Dissolve(team);
		}

		// One member left, so there is no team: drop the last member out of it as well.
		private async Task Dissolve(LiveTeam team)
		{
			foreach (var last in team.Members.ToArray())
			{
				team.Remove(last);
				_teamByCharacter.TryRemove(last, out _);
				if (CharacterOf(last) is { } member)
					await Push(last, CSPlayerInterfaceMethods.DispatchTeamLeave, TeamMemberEventCodec.Build(team.Id, MemberOf(member)));

				await _gameServerDispatcher.SendGroupMembership(last, last, team.Id, 0, false);
				await _chatServerDispatcher.RefreshTeamChannel(last, null); // the dissolve drops everyone's channel (frames 923/926)
			}

			_teams.TryRemove(team.Id, out _);
			_logger.LogInformation("Team 0x{Team:X8} dissolved.", team.Id);
		}

		// The whole roster to every member, leader's record first (the order retail uses in all six captured rosters).
		private async Task BroadcastRoster(LiveTeam team)
		{
			if (RosterOf(team.Id) is not { } roster)
				return;

			await Broadcast(team, CSPlayerInterfaceMethods.DispatchYouJoinedTeam, TeamRosterCodec.Build(roster));
		}

		// AddToGroupIIR / RemoveFromGroupIIR to every member's WORLD connection. The capture sends one record per
		// member to every member, not just one for the joiner, so a client learns the whole group either way.
		private async Task PushMembership(LiveTeam team, uint changedId, bool joined)
		{
			foreach (var recipient in team.Members)
			{
				if (joined)
				{
					// The joiner learns everyone; everyone else learns the joiner.
					foreach (var member in recipient == changedId ? team.Members : [changedId])
						await _gameServerDispatcher.SendGroupMembership(recipient, member, team.Id, team.LeaderId, true);
				}
				else
				{
					await _gameServerDispatcher.SendGroupMembership(recipient, changedId, team.Id, team.LeaderId, false);
				}
			}
		}

		// 0x2006 TeamSetLootOption(int i_eLootMode, bool i_bUseNeedGreed, int i_nNewLootThreshold): the leader changes
		// how the group splits loot. Body is [u32 mode][u8 needGreed][u32 threshold] - 9 bytes, matching the captured
		// `00000000 01 00000002` (free-for-all, need/greed on, threshold 2) - and the s2c
		// DispatchTeamLootOptionChanged carries the same three fields, so the change echoes back unchanged to every
		// member. Held on the live team only: nothing reads it yet (the server does not arbitrate loot rolls), so
		// persisting it would be storing a setting no code consults. NEEDS in-client verification of the echo.
		public async Task<bool> SetLootOption(uint leaderId, uint lootMode, bool useNeedGreed, uint lootThreshold)
		{
			if (TeamOf(leaderId) is not { } teamId || !_teams.TryGetValue(teamId, out var team))
				return false;

			if (team.LeaderId != leaderId)
			{
				_logger.LogWarning("TeamSetLootOption from {Character:X8}, who is not the leader of team 0x{Team:X8}; ignored.", leaderId, team.Id);
				return false;
			}

			team.Settings = team.Settings with { LootMode = lootMode, UseNeedGreed = useNeedGreed, LootThreshold = lootThreshold };

			await Broadcast(team, CSPlayerInterfaceMethods.DispatchTeamLootOptionChanged, TeamLootOptionCodec.Build(new TeamLootOption(lootMode, useNeedGreed, lootThreshold)));

			_logger.LogInformation("Team 0x{Team:X8}: loot mode {Mode}, need/greed {NeedGreed}, threshold {Threshold}.", team.Id, lootMode, useNeedGreed, lootThreshold);
			return true;
		}

		// --- Looking for group: the registry the team search reads (#889) --------------------------------------
		//
		// The client splits LFG in two, and so does this - they are different subjects, not two spellings of one flag:
		//   c2s SetLookingForMemberStatus(bool, message) - a LEADER advertises the TEAM     -> the team-search rows
		//   c2s SetLookingForTeamStatus(bool, message)   - a CHARACTER advertises themself  -> their own LFG status
		// Both are live CS-tier state and neither is persisted, for the same reason the roster is not: a team does not
		// survive a logout, and an advert for a session that has ended is worse than no advert.

		// The page a single DispatchTeamSearchResult carries. The client pages the request itself (SearchForTeam's
		// i_nSkipResults) and is told whether anything was left behind (i_bMoreHits), so the size is ours to pick; no
		// captured reply is non-empty, so retail's own page size is unknown. Matches MaxCharacterSearchResults. [V0]
		private const int MaxTeamSearchResults = 50;

		private readonly ConcurrentDictionary<uint, LookingForGroupStatus> _lookingForTeam = [];

		// c2s SetLookingForTeamStatus (0x2012) / GetLookingForTeamStatus (0x2014): the character's own LFG flag and
		// comment. Held per character and dropped when they disconnect (Forget).
		public LookingForGroupStatus SetLookingForTeam(uint characterId, bool lookingForTeam, string message)
		{
			var status = new LookingForGroupStatus(lookingForTeam, message);
			_lookingForTeam[characterId] = status;

			_logger.LogInformation("LFG: {Character:X8} is {State} looking for a team ('{Message}').", characterId, lookingForTeam ? "" : "no longer", message);
			return status;
		}

		public LookingForGroupStatus LookingForTeamStatusOf(uint characterId)
		{
			return _lookingForTeam.TryGetValue(characterId, out var status)
				? status
				: new LookingForGroupStatus(false, "");
		}

		// c2s SetLookingForMemberStatus (0x2013) / GetLookingForMemberStatus (0x2015): the TEAM's advert - the flag that
		// puts it in everyone else's SearchForTeam results, and the message the result row carries. Only the leader may
		// set it, the same rule the loot options follow. Null when the caller leads no team, so the caller can stay quiet
		// rather than echo a status for a team that does not exist.
		public LookingForGroupStatus? SetLookingForMembers(uint leaderId, bool lookingForMembers, string message)
		{
			if (TeamOf(leaderId) is not { } teamId || !_teams.TryGetValue(teamId, out var team))
			{
				_logger.LogInformation("SetLookingForMemberStatus from {Character:X8}, who is not in a team; ignored.", leaderId);
				return null;
			}

			if (team.LeaderId != leaderId)
			{
				_logger.LogWarning("SetLookingForMemberStatus from {Character:X8}, who is not the leader of team 0x{Team:X8}; ignored.", leaderId, team.Id);
				return null;
			}

			team.LookingForMembers = lookingForMembers;
			team.LookingForMembersMessage = message;

			_logger.LogInformation("Team 0x{Team:X8} is {State} looking for members ('{Message}').", team.Id, lookingForMembers ? "" : "no longer", message);
			return new LookingForGroupStatus(lookingForMembers, message);
		}

		public LookingForGroupStatus? LookingForMemberStatusOf(uint characterId)
		{
			return TeamOf(characterId) is { } teamId && _teams.TryGetValue(teamId, out var team)
				? new LookingForGroupStatus(team.LookingForMembers, team.LookingForMembersMessage)
				: null;
		}

		// The team's own members, so the leader's advert echoes to everyone in it.
		public Task BroadcastToTeam(uint characterId, CSPlayerInterfaceMethods opcode, byte[] body)
		{
			return TeamOf(characterId) is { } teamId && _teams.TryGetValue(teamId, out var team)
				? Broadcast(team, opcode, body)
				: Task.CompletedTask;
		}

		// c2s SearchForTeam (0x200B) -> s2c DispatchTeamSearchResult (0x200F): the LFG window's result page.
		//
		// Only teams whose leader has FLAGGED them appear. That is the subsystem's own shape rather than a filter we
		// invented: the client has a c2s method whose entire job is to advertise a team (SetLookingForMemberStatus), and
		// the result ROW carries that advert's message, so a team that never set one has nothing to put in the row. A
		// search that returns every live team would also be a privacy change, not just a laxer WHERE clause.
		//
		// Every criterion the client sends is APPLIED, and each is a reading of a request field the corpus shows but
		// never varies enough to prove [V0]:
		//   playfields    - kept if ANY member is in one of them (the client sends the playfields it wants to play in;
		//                   a team spread across two zones is in both as far as the searcher is concerned)
		//   level range   - kept if ANY member's level is inside it (the searcher is asking who they could play with,
		//                   and one in-range member is what makes a team joinable for them)
		//   member counts - the roster size, inclusive. Both captured requests cap at FIVE, one floor at 2 and one at 1,
		//                   which is the client's own slider and is applied as sent - so a full six is excluded by the
		//                   client's default search, not by us.
		// Results are ordered by team id (mint order) so paging is stable across the client's repeated requests.
		public TeamSearchResult SearchTeams(uint searcherId, IReadOnlyList<uint> playfields, uint minLevel, uint maxLevel, uint memberCountMin, uint memberCountMax, uint skipResults)
		{
			var realmId = CharacterOf(searcherId)?.RealmId ?? 0;
			var advertised = _teams.Values.Where(t => t.LookingForMembers).OrderBy(t => t.Id).ToList();

			var matches = advertised
				.Select(t => (Team: t, Roster: RosterOf(t.Id)))
				.Where(x => x.Roster != null)
				.Where(x => x.Roster!.Members.Count > 0 && CharacterOf(x.Roster.LeaderId)?.RealmId == realmId)
				.Where(x => WithinCount(x.Roster!.Members.Count, memberCountMin, memberCountMax))
				.Where(x => x.Roster!.Members.Any(m => m.Level >= minLevel && m.Level <= maxLevel))
				.Where(x => playfields.Count == 0 || x.Roster!.Members.Any(m => playfields.Contains(m.MapId)))
				.ToList();

			var page = matches.Skip((int)skipResults).Take(MaxTeamSearchResults).Select(x => new TeamSearchResultRow(x.Roster!, x.Team.LookingForMembersMessage)).ToList();
			var moreHits = matches.Count > skipResults + (uint)page.Count;

			_logger.LogDebug("SearchForTeam by {Character:X8}: {Live} live team(s), {Advertised} looking for members, {Matches} matching, sending {Page} from {Skip}{More}.", searcherId, _teams.Count, advertised.Count,
				matches.Count, page.Count, skipResults, moreHits ? " (more to come)" : "");

			return new TeamSearchResult(page, moreHits);
		}

		// The client's memberCount range, inclusive. A ZERO maximum is treated as "no ceiling": the field is a count, so
		// a literal 0 would match nothing at all, and neither captured request sends one.
		private static bool WithinCount(int count, uint min, uint max)
		{
			return count >= min && (max == 0 || count <= max);
		}

		// c2s TeamApply (0x2005): the "apply" button on a team-search row. The applicant does NOT join - retail tells
		// the LEADER, with DispatchTeamCharacterApplied(i_cWho) carrying the applicant's own member record, and the
		// leader then invites them through the ordinary TeamInvite flow. The dispatch reads through the client's
		// `FUN_005574d0` - the same member-record reader the team and raid single-character messages use (#889) - which
		// is why TeamMemberRecordCodec handles it rather than a codec of its own.
		public async Task<bool> Apply(uint applicantId, uint teamId)
		{
			if (!_teams.TryGetValue(teamId, out var team))
			{
				_logger.LogInformation("TeamApply from {Character:X8} for team 0x{Team:X8}, which does not exist.", applicantId, teamId);
				return false;
			}

			if (CharacterOf(applicantId) is not { } applicant)
				return false;

			await Push(team.LeaderId, CSPlayerInterfaceMethods.DispatchTeamCharacterApplied, TeamMemberRecordCodec.Build(MemberOf(applicant)));

			_logger.LogInformation("Team 0x{Team:X8}: {Character} applied; told the leader {Leader:X8}.", team.Id, applicant.Name, team.LeaderId);
			return true;
		}

		// A disconnecting character takes their LFG advert with them - the search answers from live sessions, so a stale
		// flag would put a row on the wire for somebody who is not there.
		public void ForgetLookingForTeam(uint characterId)
		{
			_lookingForTeam.TryRemove(characterId, out _);
		}

		private LiveTeam TeamFor(uint characterId)
		{
			if (TeamOf(characterId) is { } existing && _teams.TryGetValue(existing, out var team))
				return team;

			var created = new LiveTeam(Interlocked.Increment(ref _nextTeamId), characterId);
			_teams[created.Id] = created;
			_teamByCharacter[characterId] = created.Id;
			return created;
		}

		private Task Broadcast(LiveTeam team, CSPlayerInterfaceMethods opcode, byte[] body)
		{
			return Task.WhenAll(team.Members.Select(member => Push(member, opcode, body)));
		}

		private async Task Push(uint characterId, CSPlayerInterfaceMethods opcode, byte[] body)
		{
			if (_csClientForCharacter(characterId) is { } client)
				await Send(client, opcode, body);
		}

		// ⚠️ The RECEIVER is the recipient's OWN CSPlayerInterface instance - its character id - never instance 0.
		// Retail addresses every one of these that way (f678 -> the inviter's id, f680 -> the target's), and it is how
		// the client routes the packet to the right interface object: a push addressed to instance 0 arrives and is
		// dropped, which is why the invited client received DispatchTeamInvite, answered it with RequestTeamInvite, and
		// still showed no invite window.
		private static Task Send(INetworkClient client, CSPlayerInterfaceMethods opcode, byte[] body)
		{
			return client.SendAsync(new Rpc(PushSender, client.Endpoint(RpcInterfaces.CSPlayerInterfaceType), (ushort)opcode, body));
		}

		// The same addressed push for the sibling CS services (GroupFinderService), so every CSPlayerInterface
		// dispatch in the process carries the one sender/receiver shape the remark above pins down.
		internal static Task SendToClient(INetworkClient client, CSPlayerInterfaceMethods opcode, byte[] body)
		{
			return Send(client, opcode, body);
		}

		// One live team. Members is a snapshot-on-write list so a broadcast can iterate it while another connection
		// mutates the roster.
		private sealed class LiveTeam(uint id, uint leaderId)
		{
			public uint Id { get; } = id;
			public uint LeaderId { get; set; } = leaderId;

			// The group's loot settings (TeamSetLootOption), as the roster's own trailing block. Defaults are the ones
			// every captured roster carries.
			public TeamSettings Settings { get; set; } = TeamSettings.Default;

			// The leader's looking-for-members advert (SetLookingForMemberStatus). The flag is what puts the team in
			// everyone else's search results and the message is what the result row carries.
			public bool LookingForMembers { get; set; }
			public string LookingForMembersMessage { get; set; } = "";
			public IReadOnlyList<uint> Members { get; private set; } = [leaderId];

			public void Add(uint characterId)
			{
				if (!Members.Contains(characterId))
					Members = [.. Members, characterId];
			}

			public void Remove(uint characterId)
			{
				Members = [.. Members.Where(m => m != characterId)];
			}
		}
	}
}
