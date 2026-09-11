using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Constants;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Raid;
using Faolan.Core.Network.Codecs.Team;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Faolan.CSPlayerAgent
{
	// Raids: a set of teams rather than a set of characters, which is the shape of the whole feature. Built on
	// TeamService - the raid owns no roster of its own, it owns a list of team ids and asks TeamService for each team's
	// `cTeam` when it needs to put one on the wire.
	//
	// Like a team, a raid is ephemeral: nothing here is persisted, because a retail raid does not survive its last
	// member logging out.
	//
	// Nothing in this file is capture-proven. A sweep of all 120 dumps finds zero raid traffic - no captured session
	// ever formed a raid - so the message bodies come from the client's own readers (every codec used here says which
	// reader) and the routing is the team flow's, one level up. That analogy is not evidence, and it is recorded as
	// such in docs/protocol/gaps.md; what it is not is invention, because every method, its parameters and its body
	// layout are the client's.
	public sealed class RaidService
	{
		// Raid instances are minted the way team ones are - sequentially from a base, opaque to the client, only
		// their identity matters. A separate base from TeamService's so a raid id and a team id never collide in a
		// log line; retail's own numbering is unknown. [V0]
		private const uint FirstRaidId = 0x00600000;

		// The sender of every CsPlayerAgent s2c packet: the guild/team server object - see TeamService.PushSender.
		private static readonly ProtoEndpoint PushSender = Endpoints.Endpoint(RpcInterfaces.CSPlayerAgentType, 0x04);

		private readonly ConcurrentDictionary<uint, LiveRaid> _raids = [];
		private readonly ConcurrentDictionary<uint, uint> _raidByTeam = [];

		// invitee -> inviter, for the window between RaidInviteCharacter and RaidAcceptInvite. Keyed by character even
		// for RaidInviteTeam, because it is a person who answers the box.
		private readonly ConcurrentDictionary<uint, uint> _pendingInvites = [];
		private readonly ConnectionRegistry _connections;
		private readonly ILogger _logger;
		private readonly TeamService _teams;
		private uint _nextRaidId = FirstRaidId;

		public RaidService(ILogger<CsPlayerAgentListener> logger, TeamService teams, [FromKeyedServices(CsPlayerAgentServices.ConnectionsKey)] ConnectionRegistry connections)
		{
			_logger = logger;
			_teams = teams;
			_connections = connections;
		}

		// The raid a character is in, via their team. Membership is transitive by construction: a raid holds teams
		// and a team holds characters, so there is no second membership table to keep in step.
		public uint? RaidOf(uint characterId)
		{
			return _teams.TeamOf(characterId) is { } teamId && _raidByTeam.TryGetValue(teamId, out var raidId)
				? raidId
				: null;
		}

		public IReadOnlyList<uint> TeamsOf(uint raidId)
		{
			return _raids.TryGetValue(raidId, out var raid)
				? raid.Teams
				: [];
		}

		// Everyone in the raid - the union of its teams' members.
		public IReadOnlyList<uint> MembersOf(uint raidId)
		{
			return [.. TeamsOf(raidId).SelectMany(_teams.MembersOf)];
		}

		// c2s RaidCreate(): the caller's team becomes a one-team raid. A solo player gets a team first - a raid is
		// made of teams, so there is nothing to put in it otherwise.
		public async Task<bool> Create(uint characterId)
		{
			if (RaidOf(characterId) != null)
			{
				_logger.LogInformation("RaidCreate from {Character:X8}, who is already in a raid; ignored.", characterId);
				return false;
			}

			var teamId = _teams.EnsureTeam(characterId);
			var raid = new LiveRaid(Interlocked.Increment(ref _nextRaidId), teamId);
			_raids[raid.Id] = raid;
			_raidByTeam[teamId] = raid.Id;

			await BroadcastMatch(raid);
			_logger.LogInformation("Raid 0x{Raid:X8} created by {Character:X8} (team 0x{Team:X8}).", raid.Id, characterId, teamId);

			return true;
		}

		// c2s RaidInviteCharacter(ID32, string) / RaidInviteCharacterByName(string, string): ask one player in. The
		// invite is answered by them, and it is their whole team that joins - which is what RaidAcceptInvite does.
		public async Task Invite(uint inviterId, Character target, string message = "")
		{
			if (RaidOf(inviterId) is not { } raidId)
			{
				// Inviting someone while not in a raid forms one, the way a team invite from a solo player does.
				if (!await Create(inviterId))
					return;

				raidId = RaidOf(inviterId)!.Value;
			}

			if (RaidOf(target.Id) != null)
			{
				await Push(inviterId, CSPlayerInterfaceMethods.DispatchRaidIsAlreadyInRaid, MemberBody(target.Id));
				return;
			}

			_pendingInvites[target.Id] = inviterId;

			if (_teams.MemberRecordOf(inviterId) is { } inviter)
				await Push(target.Id, CSPlayerInterfaceMethods.DispatchRaidInvite, TeamInviteCodec.Build(inviter, message));

			_logger.LogInformation("Raid 0x{Raid:X8}: {Inviter:X8} invited {Target}.", raidId, inviterId, target.Name);
		}

		// c2s RaidInviteTeam(ID32 teamID): the same invite aimed at a whole team. The team's leader is the one shown
		// the box, so this reduces to inviting them.
		public async Task InviteTeam(uint inviterId, uint teamId)
		{
			if (_teams.LeaderOf(teamId) is not { } leaderId || CharacterOf(leaderId) is not { } leader)
			{
				_logger.LogWarning("RaidInviteTeam for team 0x{Team:X8} with no online leader.", teamId);
				return;
			}

			await Invite(inviterId, leader);
		}

		// c2s RaidAcceptInvite(ID32) / RaidInviteTeamAccept(ID32): the accepter's whole team joins the inviter's raid.
		public async Task<bool> AcceptInvite(uint accepterId)
		{
			if (!_pendingInvites.TryRemove(accepterId, out var inviterId))
			{
				_logger.LogWarning("RaidAcceptInvite from {Character:X8} with no pending invite.", accepterId);
				return false;
			}

			if (RaidOf(inviterId) is not { } raidId || !_raids.TryGetValue(raidId, out var raid))
				return false;

			var teamId = _teams.EnsureTeam(accepterId);
			if (!raid.AddTeam(teamId))
				return false;

			_raidByTeam[teamId] = raid.Id;

			// The joining team learns the whole raid; the raid learns the joining team. Both directions have a team
			// counterpart that captures prove (DispatchYouJoinedTeam to everyone, DispatchTeamJoin naming the
			// joiner), and this is that pair one level up. [V0 on the routing]
			await PushMatch(raid, _teams.MembersOf(teamId));
			if (_teams.RosterOf(teamId) is { } roster)
				await Broadcast(raid, CSPlayerInterfaceMethods.DispatchRaidTeamJoined, TeamRosterCodec.Build(roster), _teams.MembersOf(teamId));

			_logger.LogInformation("Raid 0x{Raid:X8}: team 0x{Team:X8} joined ({Count} team(s)).", raid.Id, teamId, raid.Teams.Count);

			return true;
		}

		// c2s RaidDeclineInvite(ID32) / RaidInviteTeamDecline(ID32): tell the inviter, forget the invite.
		public async Task Decline(uint declinerId)
		{
			if (!_pendingInvites.TryRemove(declinerId, out var inviterId))
				return;

			await Push(inviterId, CSPlayerInterfaceMethods.DispatchRaidTargetDeclinedRaidInvite, MemberBody(declinerId));
		}

		// c2s RaidLeave(): the caller leaves, and leaves their team too - a raid slot is a seat in one of its teams, so
		// there is no "in the raid but in no team" state to leave them in. The team goes with them when they were its
		// last member.
		public async Task<bool> Leave(uint characterId)
		{
			if (RaidOf(characterId) is not { } raidId || !_raids.TryGetValue(raidId, out var raid) || _teams.TeamOf(characterId) is not { } teamId)
				return false;

			await _teams.Leave(characterId);
			await AfterTeamShrank(raid, teamId);

			return true;
		}

		// c2s RaidKickCharacter(ID32): raid-leader-only, and it is the same departure a RaidLeave produces.
		public async Task<bool> KickCharacter(uint leaderId, uint targetId)
		{
			if (!IsRaidLeader(leaderId, out var raid) || RaidOf(targetId) != raid.Id)
				return false;

			await Push(targetId, CSPlayerInterfaceMethods.DispatchRaidYouAreNoLongerInvited, MemberBody(targetId));

			return await Leave(targetId);
		}

		// c2s RaidKickTeam(ID32 teamID): raid-leader-only. The team survives as a team; it is only out of the raid.
		public async Task<bool> KickTeam(uint leaderId, uint teamId)
		{
			if (!IsRaidLeader(leaderId, out var raid) || !raid.Teams.Contains(teamId))
				return false;

			var leaving = _teams.MembersOf(teamId).ToArray();
			raid.RemoveTeam(teamId);
			_raidByTeam.TryRemove(teamId, out _);

			var body = EntityIdCodec.Build(EntityIds.Team(teamId));
			await Broadcast(raid, CSPlayerInterfaceMethods.DispatchRaidTeamLeft, body);
			foreach (var member in leaving)
				await Push(member, CSPlayerInterfaceMethods.DispatchRaidTeamLeft, body);

			await PromoteIfLeaderTeamLeft(raid, teamId);
			_logger.LogInformation("Raid 0x{Raid:X8}: team 0x{Team:X8} kicked ({Count} team(s) left).", raid.Id, teamId, raid.Teams.Count);

			return await DissolveIfEmpty(raid);
		}

		// c2s RaidMoveTeamMember(ID32 characterID, ID32 destinationTeamID): move a raider between two of the raid's
		// own teams. Both teams change, so both are re-announced.
		public async Task<bool> MoveMember(uint leaderId, uint characterId, uint destinationTeamId)
		{
			if (!IsRaidLeader(leaderId, out var raid) || RaidOf(characterId) != raid.Id || !raid.Teams.Contains(destinationTeamId))
				return false;

			var sourceTeamId = _teams.TeamOf(characterId);
			if (!_teams.MoveMember(characterId, destinationTeamId))
				return false;

			await AnnounceTeam(raid, sourceTeamId);
			await AnnounceTeam(raid, destinationTeamId);

			return true;
		}

		// c2s RaidSwapCharacters(ID32, ID32): exchange two raiders' teams. Done as two moves through a scratch step
		// so neither team is ever seen holding both.
		public async Task<bool> Swap(uint leaderId, uint firstId, uint secondId)
		{
			if (!IsRaidLeader(leaderId, out var raid) || RaidOf(firstId) != raid.Id || RaidOf(secondId) != raid.Id)
				return false;

			if (_teams.TeamOf(firstId) is not { } firstTeam || _teams.TeamOf(secondId) is not { } secondTeam || firstTeam == secondTeam)
				return false;

			if (!_teams.MoveMember(firstId, secondTeam) || !_teams.MoveMember(secondId, firstTeam))
				return false;

			await AnnounceTeam(raid, firstTeam);
			await AnnounceTeam(raid, secondTeam);

			return true;
		}

		// c2s RaidSetLootOption(int, bool, int): raid-leader-only, echoed to the whole raid - the exact shape of
		// TeamSetLootOption, which is captured, on the same three-field body.
		public async Task<bool> SetLootOption(uint leaderId, uint lootMode, bool useNeedGreed, uint lootThreshold)
		{
			if (!IsRaidLeader(leaderId, out var raid))
				return false;

			raid.Settings = raid.Settings with { LootMode = lootMode, UseNeedGreed = useNeedGreed, LootThreshold = lootThreshold };
			await Broadcast(raid, CSPlayerInterfaceMethods.DispatchRaidLootOptionChanged, TeamLootOptionCodec.Build(new TeamLootOption(lootMode, useNeedGreed, lootThreshold)));

			return true;
		}

		// c2s RaidSetMasterLooter(ID32): raid-leader-only. The s2c body is the character record, not their id - the
		// client reads `i_cWho` with the cCharacter reader.
		public async Task<bool> SetMasterLooter(uint leaderId, uint characterId)
		{
			if (!IsRaidLeader(leaderId, out var raid) || RaidOf(characterId) != raid.Id)
				return false;

			raid.Settings = raid.Settings with { MasterLooter = EntityIds.Character(characterId) };
			await Broadcast(raid, CSPlayerInterfaceMethods.DispatchRaidNewMasterLooter, MemberBody(characterId));

			return true;
		}

		// A member left their team: the raid either loses the team outright or shows it a member lighter.
		private async Task AfterTeamShrank(LiveRaid raid, uint teamId)
		{
			if (_teams.RosterOf(teamId) is { } roster)
			{
				await Broadcast(raid, CSPlayerInterfaceMethods.DispatchRaidTeamUpdate, TeamRosterCodec.Build(roster));
				return;
			}

			raid.RemoveTeam(teamId);
			_raidByTeam.TryRemove(teamId, out _);
			await Broadcast(raid, CSPlayerInterfaceMethods.DispatchRaidTeamLeft, EntityIdCodec.Build(EntityIds.Team(teamId)));
			await PromoteIfLeaderTeamLeft(raid, teamId);
			await DissolveIfEmpty(raid);
		}

		private Task AnnounceTeam(LiveRaid raid, uint? teamId)
		{
			return teamId is { } id && _teams.RosterOf(id) is { } roster
				? Broadcast(raid, CSPlayerInterfaceMethods.DispatchRaidTeamUpdate, TeamRosterCodec.Build(roster))
				: Task.CompletedTask;
		}

		// The raid leader is a team, not a character - DispatchRaidNewRaidLeader carries an `i_cTeamID`. So when the
		// leading team leaves, the next team takes it.
		private async Task PromoteIfLeaderTeamLeft(LiveRaid raid, uint departedTeamId)
		{
			if (raid.LeaderTeamId != departedTeamId || raid.Teams.Count == 0)
				return;

			raid.LeaderTeamId = raid.Teams[0];
			await Broadcast(raid, CSPlayerInterfaceMethods.DispatchRaidNewRaidLeader, EntityIdCodec.Build(EntityIds.Team(raid.LeaderTeamId)));
		}

		// One team left is no raid, exactly as one member is no team.
		private async Task<bool> DissolveIfEmpty(LiveRaid raid)
		{
			if (raid.Teams.Count > 1)
				return true;

			foreach (var teamId in raid.Teams.ToArray())
			{
				raid.RemoveTeam(teamId);
				_raidByTeam.TryRemove(teamId, out _);
				foreach (var member in _teams.MembersOf(teamId))
					await Push(member, CSPlayerInterfaceMethods.DispatchRaidTeamLeft, EntityIdCodec.Build(EntityIds.Team(teamId)));
			}

			_raids.TryRemove(raid.Id, out _);
			_logger.LogInformation("Raid 0x{Raid:X8} dissolved.", raid.Id);

			return true;
		}

		private bool IsRaidLeader(uint characterId, out LiveRaid raid)
		{
			raid = null!;
			if (RaidOf(characterId) is not { } raidId || !_raids.TryGetValue(raidId, out var found))
				return false;

			raid = found;
			if (_teams.TeamOf(characterId) == raid.LeaderTeamId && _teams.LeaderOf(raid.LeaderTeamId) == characterId)
				return true;

			_logger.LogWarning("Raid action from {Character:X8}, who does not lead raid 0x{Raid:X8}; ignored.", characterId, raid.Id);

			return false;
		}

		// The whole raid as `cMatch`: a header cTeam carrying the raid's id, leader and settings, then one cTeam per
		// member team. The header's own member list is empty - the people are in the sub-teams, and listing them
		// twice would be the client's problem to reconcile. [V0: no capture carries a cMatch]
		private byte[] MatchBody(LiveRaid raid)
		{
			var teams = raid.Teams.Select(_teams.RosterOf).OfType<TeamRoster>().ToList();
			var leader = _teams.LeaderOf(raid.LeaderTeamId) ?? 0;

			return RaidMatchCodec.Build(new TeamRoster(raid.Id, leader, [], raid.Settings), teams);
		}

		private Task BroadcastMatch(LiveRaid raid)
		{
			return PushMatch(raid, MembersOf(raid.Id));
		}

		private async Task PushMatch(LiveRaid raid, IReadOnlyList<uint> recipients)
		{
			var body = MatchBody(raid);
			foreach (var recipient in recipients)
				await Push(recipient, CSPlayerInterfaceMethods.DispatchYouJoinedRaid, body);
		}

		private Character? CharacterOf(uint characterId)
		{
			return _connections.ForCharacter(characterId)?.Session.Character;
		}

		// A character as the wire's `cCharacter` - taken from the live session rather than from a roster, because
		// half of these messages name someone who is not in a team at all (an invitee, a decliner). An empty body
		// for someone who has gone offline mid-exchange, which `Push` then drops along with the rest of the message.
		private byte[] MemberBody(uint characterId)
		{
			return _teams.MemberRecordOf(characterId) is { } member ? TeamMemberRecordCodec.Build(member) : [];
		}

		private async Task Broadcast(LiveRaid raid, CSPlayerInterfaceMethods opcode, byte[] body, IReadOnlyList<uint>? except = null)
		{
			foreach (var member in MembersOf(raid.Id))
			{
				if (except == null || !except.Contains(member))
					await Push(member, opcode, body);
			}
		}

		private async Task Push(uint characterId, CSPlayerInterfaceMethods opcode, byte[] body)
		{
			if (body.Length == 0)
				return;

			// The receiver is the recipient's own CSPlayerInterface instance - its character id, never instance 0. See
			// the same note on TeamService.Send: a push addressed to instance 0 arrives and is dropped.
			if (_connections.ForCharacter(characterId) is { } client)
				await client.SendAsync(new Rpc(PushSender, client.Endpoint(RpcInterfaces.CSPlayerInterfaceType), (ushort)opcode, body));
		}

		// One live raid: an ordered list of team ids and the settings block its `cTeam` header carries.
		private sealed class LiveRaid(uint id, uint leaderTeamId)
		{
			public uint Id { get; } = id;
			public uint LeaderTeamId { get; set; } = leaderTeamId;
			public TeamSettings Settings { get; set; } = TeamSettings.Default;
			public IReadOnlyList<uint> Teams { get; private set; } = [leaderTeamId];

			public bool AddTeam(uint teamId)
			{
				if (Teams.Contains(teamId))
					return false;

				Teams = [.. Teams, teamId];

				return true;
			}

			public void RemoveTeam(uint teamId)
			{
				Teams = [.. Teams.Where(t => t != teamId)];
			}
		}
	}
}
