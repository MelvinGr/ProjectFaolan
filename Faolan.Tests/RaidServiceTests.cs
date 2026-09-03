using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Raid;
using Faolan.Core.Network.Codecs.Team;
using Faolan.Core.Network.Codecs.Text;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Network.Server.ChatServer;
using Faolan.Core.Network.Server.GameServer;
using Faolan.CSPlayerAgent;
using Faolan.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Faolan.Tests
{
	// The RAID state machine (#669) - create, invite, accept, decline, kick, move, swap, dissolve - and the messages
	// each transition puts on the wire.
	//
	// ⚠️ Unlike TeamServiceTests, there is no capture behind this: no session in the corpus ever formed a raid. What
	// these gates pin is that our behaviour matches the SHAPE the client's own method table describes - a raid is a
	// set of teams, its leader is a TEAM, and each message carries the body its reader expects. They are a
	// regression fence, not evidence about retail.
	public class RaidServiceTests
	{
		private sealed class StubGameDispatcher : IGameServerDispatcher
		{
			public Task<bool> SendGroupMembership(uint recipientId, uint memberId, uint teamId, uint leaderId, bool joined) => Task.FromResult(true);
			public Task<bool> CreditCharacterCash(uint characterId, ulong amountTin) => Task.FromResult(false);
			public bool DisconnectCharacter(uint characterId) => false;
			public CharacterLiveState? GetCharacterLiveState(uint characterId) => null;
			public IEnumerable<INetworkClientSession> GetWorldSessions() => [];
			public IReadOnlyList<ZoneEntityInfo> GetZoneEntities(uint mapId) => [];
			public Task<bool> RunGmCommand(uint characterId, string commandText) => Task.FromResult(false);
			public Task<bool> TeleportCharacter(uint characterId, uint mapId, System.Numerics.Vector3? position = null) => Task.FromResult(false);
			public Task<bool> SendGuildTag(uint characterId, string guildName) => Task.FromResult(false);
			public Task<bool> SendMinigameMatchEnded(uint characterId, uint winningSide, MinigameResult yourResult) => Task.FromResult(false);
			public Task<bool> SendMinigameForceStop(uint characterId, string message) => Task.FromResult(false);
			public void Register(IGameServerProvider provider) { }
			public void Unregister(IGameServerProvider provider) { }
		}

		private sealed class StubChatDispatcher : IChatServerDispatcher
		{
			public Task RefreshTeamChannel(uint characterId, uint? teamId) => Task.CompletedTask;
			public Task CharacterSay(uint characterId, ChatMarkup message, IReadOnlyCollection<uint>? recipientCharacterIds = null) => Task.CompletedTask;
			public Task NpcBark(uint npcHandle, NpcChatterBark bark, IReadOnlyCollection<uint>? recipientCharacterIds = null) => Task.CompletedTask;
			public IEnumerable<INetworkClientSession> GetWorldSessions() => [];
			public Task<int> SendClaudeChannelMessage(ChatMarkup message) => Task.FromResult(0);
			public Task RefreshPlayfieldChannel(uint characterId, uint mapId) => Task.CompletedTask;
			public Task RefreshGuildChannel(uint characterId, uint? guildId) => Task.CompletedTask;
			public Task<bool> SendClaudeWhisper(uint targetCharacterId, ChatMarkup message) => Task.FromResult(false);
			public Task SendSystemMessage(uint characterId, ChatMarkup message) => Task.CompletedTask;
			public Task SystemBroadcast(ChatMarkup message) => Task.CompletedTask;
			public void Register(IChatServerProvider provider) { }
			public void Unregister(IChatServerProvider provider) { }
		}

		private static (TeamService Teams, RaidService Raids, Dictionary<uint, RecordingNetworkClient> Clients) Harness(params uint[] characterIds)
		{
			var clients = new Dictionary<uint, RecordingNetworkClient>();
			var connections = new ConnectionRegistry();
			var teams = new TeamService(NullLogger<CsPlayerAgentListener>.Instance, new StubGameDispatcher(), new StubChatDispatcher(), connections);
			var raids = new RaidService(NullLogger<CsPlayerAgentListener>.Instance, teams, connections);

			foreach (var id in characterIds)
			{
				var client = new RecordingNetworkClient(id);
				client.Session.Character!.Name = $"C{id}";
				client.Session.Character.Class = ConanClass.Necromancer;
				clients[id] = client;
				connections.Add(client);
			}

			return (teams, raids, clients);

		}

		private static Character CharacterOf(RecordingNetworkClient client)
		{
			return client.Session.Character!;
		}

		// Every s2c method a client was sent, in order.
		private static List<CSPlayerInterfaceMethods> MethodsSentTo(RecordingNetworkClient client)
		{
			return [.. client.Sent.Select(packet => (CSPlayerInterfaceMethods)Rpc.Parse(packet).Method)];
		}

		private static byte[] BodySentTo(RecordingNetworkClient client, CSPlayerInterfaceMethods method)
		{
			return client.Sent.Select(Rpc.Parse).First(rpc => rpc.Method == (uint)method).Body.ToArray();
		}

		// A solo player forming a raid gets a TEAM first: a raid holds teams, so there is nothing to put in it
		// otherwise. Retail's own raid window shows you sitting in a group of one, which is the same statement.
		[Fact]
		public async Task Create_PutsASoloPlayerInATeamInsideTheRaid()
		{
			var (teams, raids, clients) = Harness(1);

			Assert.True(await raids.Create(1));

			var raidId = raids.RaidOf(1);
			Assert.NotNull(raidId);
			Assert.Equal([teams.TeamOf(1)!.Value], raids.TeamsOf(raidId!.Value));
			Assert.Equal([1u], raids.MembersOf(raidId.Value));
			Assert.Contains(CSPlayerInterfaceMethods.DispatchYouJoinedRaid, MethodsSentTo(clients[1]));
		}

		[Fact]
		public async Task CreateTwice_IsRefused()
		{
			var (_, raids, _) = Harness(1);
			await raids.Create(1);

			Assert.False(await raids.Create(1));
		}

		// The invite is answered by a PERSON and joins their whole TEAM: two already-grouped players both end up in
		// the raid off one accept, in one team.
		[Fact]
		public async Task AcceptInvite_BringsTheAcceptersWholeTeam()
		{
			var (teams, raids, clients) = Harness(1, 2, 3);
			await teams.Invite(clients[2], CharacterOf(clients[2]), CharacterOf(clients[3]));
			await teams.AcceptInvite(3);

			await raids.Create(1);
			await raids.Invite(1, CharacterOf(clients[2]));
			Assert.True(await raids.AcceptInvite(2));

			var raidId = raids.RaidOf(1)!.Value;
			Assert.Equal(raidId, raids.RaidOf(3));
			Assert.Equal(2, raids.TeamsOf(raidId).Count);
			Assert.Equal([1u, 2u, 3u], raids.MembersOf(raidId).Order());

			// The joining side learns the whole raid; the side already in it learns the new team.
			Assert.Contains(CSPlayerInterfaceMethods.DispatchYouJoinedRaid, MethodsSentTo(clients[2]));
			Assert.Contains(CSPlayerInterfaceMethods.DispatchRaidTeamJoined, MethodsSentTo(clients[1]));
			Assert.DoesNotContain(CSPlayerInterfaceMethods.DispatchRaidTeamJoined, MethodsSentTo(clients[2]));
		}

		// The inviter comes from OUR pending table, not the echoed body - the same rule AcceptTeamInvite follows, so
		// a forged accept cannot pull anyone into a raid.
		[Fact]
		public async Task AcceptWithoutAnInvite_DoesNothing()
		{
			var (_, raids, _) = Harness(1, 2);
			await raids.Create(1);

			Assert.False(await raids.AcceptInvite(2));
			Assert.Null(raids.RaidOf(2));
		}

		[Fact]
		public async Task Decline_TellsTheInviterAndDropsTheInvite()
		{
			var (_, raids, clients) = Harness(1, 2);
			await raids.Create(1);
			await raids.Invite(1, CharacterOf(clients[2]));

			await raids.Decline(2);

			Assert.Contains(CSPlayerInterfaceMethods.DispatchRaidTargetDeclinedRaidInvite, MethodsSentTo(clients[1]));
			Assert.False(await raids.AcceptInvite(2));
		}

		// Inviting someone who is already raiding answers the INVITER with DispatchRaidIsAlreadyInRaid - the client
		// has a message for exactly this, so it is the one to send rather than silence.
		[Fact]
		public async Task InvitingSomeoneAlreadyRaiding_SaysSo()
		{
			var (_, raids, clients) = Harness(1, 2);
			await raids.Create(2);
			await raids.Create(1);

			await raids.Invite(1, CharacterOf(clients[2]));

			Assert.Contains(CSPlayerInterfaceMethods.DispatchRaidIsAlreadyInRaid, MethodsSentTo(clients[1]));
			Assert.NotEqual(raids.RaidOf(1), raids.RaidOf(2));
		}

		// A member leaving a MULTI-person team leaves the team in the raid, one member lighter - so the raid sees an
		// update, not a departure.
		[Fact]
		public async Task LeavingAMultiMemberTeam_UpdatesTheTeamRatherThanRemovingIt()
		{
			var (teams, raids, clients) = Harness(1, 2, 3, 4);
			await teams.Invite(clients[2], CharacterOf(clients[2]), CharacterOf(clients[3]));
			await teams.AcceptInvite(3);
			await teams.Invite(clients[2], CharacterOf(clients[2]), CharacterOf(clients[4]));
			await teams.AcceptInvite(4);

			await raids.Create(1);
			await raids.Invite(1, CharacterOf(clients[2]));
			await raids.AcceptInvite(2);

			var raidId = raids.RaidOf(1)!.Value;
			Assert.True(await raids.Leave(4));

			Assert.Equal(2, raids.TeamsOf(raidId).Count);
			Assert.Null(raids.RaidOf(4));
			Assert.Contains(CSPlayerInterfaceMethods.DispatchRaidTeamUpdate, MethodsSentTo(clients[1]));
		}

		// Down to one team is no raid, the way one member is no team.
		[Fact]
		public async Task TheRaidDissolvesWhenItIsDownToOneTeam()
		{
			var (_, raids, clients) = Harness(1, 2);
			await raids.Create(1);
			await raids.Invite(1, CharacterOf(clients[2]));
			await raids.AcceptInvite(2);

			Assert.True(await raids.Leave(2));

			Assert.Null(raids.RaidOf(1));
			Assert.Null(raids.RaidOf(2));
			Assert.Contains(CSPlayerInterfaceMethods.DispatchRaidTeamLeft, MethodsSentTo(clients[1]));
		}

		// The raid leader is a TEAM - DispatchRaidNewRaidLeader carries an `i_cTeamID`, not a character - so the
		// promotion moves between teams and the body is a team ID32.
		[Fact]
		public async Task WhenTheLeadingTeamLeaves_TheNextTeamLeads()
		{
			var (teams, raids, clients) = Harness(1, 2, 3);
			await raids.Create(1);
			await raids.Invite(1, CharacterOf(clients[2]));
			await raids.AcceptInvite(2);
			await raids.Invite(1, CharacterOf(clients[3]));
			await raids.AcceptInvite(3);

			var raidId = raids.RaidOf(1)!.Value;
			var survivor = teams.TeamOf(2)!.Value;
			Assert.True(await raids.Leave(1));

			Assert.Equal(2, raids.TeamsOf(raidId).Count);

			var body = EntityIdCodec.Parse(BodySentTo(clients[2], CSPlayerInterfaceMethods.DispatchRaidNewRaidLeader));
			Assert.Equal(survivor, body.Id);
		}

		[Fact]
		public async Task OnlyTheRaidLeaderCanKickATeam()
		{
			var (teams, raids, clients) = Harness(1, 2);
			await raids.Create(1);
			await raids.Invite(1, CharacterOf(clients[2]));
			await raids.AcceptInvite(2);

			Assert.False(await raids.KickTeam(2, teams.TeamOf(1)!.Value));
			Assert.NotNull(raids.RaidOf(1));
		}

		// MoveTeamMember re-seats a raider in another of the raid's own teams. Both teams change, and the source team
		// survives at one member - inside a raid a group of one is a legitimate slot, which is why RaidService uses
		// TeamService.MoveMember rather than Leave+join.
		[Fact]
		public async Task MoveMember_ReSeatsARaiderWithoutDissolvingTheSourceTeam()
		{
			var (teams, raids, clients) = Harness(1, 2, 3);
			await teams.Invite(clients[2], CharacterOf(clients[2]), CharacterOf(clients[3]));
			await teams.AcceptInvite(3);

			await raids.Create(1);
			await raids.Invite(1, CharacterOf(clients[2]));
			await raids.AcceptInvite(2);

			var leaderTeam = teams.TeamOf(1)!.Value;
			var otherTeam = teams.TeamOf(2)!.Value;

			Assert.True(await raids.MoveMember(1, 3, leaderTeam));

			Assert.Equal(leaderTeam, teams.TeamOf(3));
			Assert.Equal([2u], teams.MembersOf(otherTeam));
			Assert.Equal(2, raids.TeamsOf(raids.RaidOf(1)!.Value).Count);
		}

		[Fact]
		public async Task Swap_ExchangesTwoRaidersTeams()
		{
			var (teams, raids, clients) = Harness(1, 2);
			await raids.Create(1);
			await raids.Invite(1, CharacterOf(clients[2]));
			await raids.AcceptInvite(2);

			var first = teams.TeamOf(1)!.Value;
			var second = teams.TeamOf(2)!.Value;

			Assert.True(await raids.Swap(1, 1, 2));

			Assert.Equal(second, teams.TeamOf(1));
			Assert.Equal(first, teams.TeamOf(2));
		}

		// The loot settings ride the raid's own `cTeam` header, so setting them changes what a later
		// DispatchYouJoinedRaid carries - not just the echo.
		[Fact]
		public async Task SetLootOption_EchoesAndSticksToTheRaidHeader()
		{
			var (_, raids, clients) = Harness(1, 2, 3);
			await raids.Create(1);
			await raids.Invite(1, CharacterOf(clients[2]));
			await raids.AcceptInvite(2);

			Assert.True(await raids.SetLootOption(1, 2, false, 4));
			Assert.Contains(CSPlayerInterfaceMethods.DispatchRaidLootOptionChanged, MethodsSentTo(clients[2]));

			await raids.Invite(1, CharacterOf(clients[3]));
			await raids.AcceptInvite(3);

			var match = RaidMatchCodec.Parse(BodySentTo(clients[3], CSPlayerInterfaceMethods.DispatchYouJoinedRaid));
			Assert.Equal(2u, match.Raid.Settings.LootMode);
			Assert.False(match.Raid.Settings.UseNeedGreed);
			Assert.Equal(4u, match.Raid.Settings.LootThreshold);
		}

		[Fact]
		public async Task SetMasterLooter_LandsInTheRaidHeader()
		{
			var (_, raids, clients) = Harness(1, 2, 3);
			await raids.Create(1);
			await raids.Invite(1, CharacterOf(clients[2]));
			await raids.AcceptInvite(2);

			Assert.True(await raids.SetMasterLooter(1, 2));

			await raids.Invite(1, CharacterOf(clients[3]));
			await raids.AcceptInvite(3);

			var match = RaidMatchCodec.Parse(BodySentTo(clients[3], CSPlayerInterfaceMethods.DispatchYouJoinedRaid));
			Assert.Equal(2u, match.Raid.Settings.MasterLooter.Id);
		}

		// The cMatch a real raid produces must be readable by its own Parse - the round-trip is all the proof there
		// can be without a capture, and it is what would catch a list frame or a field width going wrong.
		[Fact]
		public async Task TheRaidWeBuild_RoundTripsThroughItsOwnCodec()
		{
			var (teams, raids, clients) = Harness(1, 2, 3);
			await teams.Invite(clients[2], CharacterOf(clients[2]), CharacterOf(clients[3]));
			await teams.AcceptInvite(3);

			await raids.Create(1);
			await raids.Invite(1, CharacterOf(clients[2]));
			await raids.AcceptInvite(2);

			var body = BodySentTo(clients[2], CSPlayerInterfaceMethods.DispatchYouJoinedRaid);
			var match = RaidMatchCodec.Parse(body);

			Assert.Equal(2, match.Teams.Count);
			Assert.Equal(3, match.Teams.Sum(t => t.Members.Count));
			Assert.Empty(match.Raid.Members); // the header names the raid, the people are in the sub-teams
			Assert.Equal(Convert.ToHexString(body), Convert.ToHexString(RaidMatchCodec.Build(match)));
		}
	}
}
