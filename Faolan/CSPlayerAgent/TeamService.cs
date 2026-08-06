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
			var ordered = team.Members.OrderByDescending(id => id == team.LeaderId).ToList();
			var records = ordered.Select(CharacterOf).OfType<Character>().Select(c => MemberOf(c)).ToList();
			if (records.Count == 0)
				return;

			var body = TeamRosterCodec.Build(team.Id, team.LeaderId, records);
			await Broadcast(team, CSPlayerInterfaceMethods.DispatchYouJoinedTeam, body);
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

			team.LootMode = lootMode;
			team.UseNeedGreed = useNeedGreed;
			team.LootThreshold = lootThreshold;

			await Broadcast(team, CSPlayerInterfaceMethods.DispatchTeamLootOptionChanged, TeamLootOptionCodec.Build(new TeamLootOption(lootMode, useNeedGreed, lootThreshold)));

			_logger.LogInformation("Team 0x{Team:X8}: loot mode {Mode}, need/greed {NeedGreed}, threshold {Threshold}.", team.Id, lootMode, useNeedGreed, lootThreshold);
			return true;
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

		// One live team. Members is a snapshot-on-write list so a broadcast can iterate it while another connection
		// mutates the roster.
		private sealed class LiveTeam(uint id, uint leaderId)
		{
			public uint Id { get; } = id;
			public uint LeaderId { get; set; } = leaderId;

			// The group's loot settings (TeamSetLootOption). Defaults are the ones every captured roster carries.
			public uint LootMode { get; set; }
			public bool UseNeedGreed { get; set; } = true;
			public uint LootThreshold { get; set; } = 2;
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
