using System.Collections.Generic;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Server.GameServer;
using Faolan.CSPlayerAgent;
using Faolan.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Faolan.Tests
{
	// The team ROSTER transitions - invite, accept, leave, kick, promote, dissolve. The wire bytes are gated separately
	// (TeamTradeFriendCodecTests, byte-exact against the capture); this is the state machine those bytes describe, and
	// the reason it is worth its own gate is that a team spans two listener tiers, so nothing else would notice a
	// roster that goes stale on one of them.
	public class TeamServiceTests
	{
		// Records which world-connection group-membership pushes were made, standing in for the GameServer listener.
		private sealed class RecordingDispatcher : IGameServerDispatcher
		{
			public List<(uint Recipient, uint Member, uint Team, bool Joined)> Membership { get; } = [];

			public Task<bool> SendGroupMembership(uint recipientId, uint memberId, uint teamId, uint leaderId, bool joined)
			{
				Membership.Add((recipientId, memberId, teamId, joined));
				return Task.FromResult(true);
			}

			public bool DisconnectCharacter(uint characterId) => false;
			public CharacterLiveState? GetCharacterLiveState(uint characterId) => null;
			public IEnumerable<INetworkClientSession> GetWorldSessions() => [];
			public IReadOnlyList<ZoneEntityInfo> GetZoneEntities(uint mapId) => [];
			public Task<bool> RunGmCommand(uint characterId, string commandText) => Task.FromResult(false);
			public Task<bool> SendGuildTag(uint characterId, string guildName) => Task.FromResult(false);
			public void Register(IGameServerProvider provider) { }
			public void Unregister(IGameServerProvider provider) { }
		}

		private static (TeamService Teams, RecordingDispatcher Dispatcher, Dictionary<uint, RecordingNetworkClient> Clients) Harness(params uint[] characterIds)
		{
			var dispatcher = new RecordingDispatcher();
			var clients = new Dictionary<uint, RecordingNetworkClient>();
			var teams = new TeamService(NullLogger.Instance, dispatcher, id => clients.TryGetValue(id, out var c)
				? c
				: null);

			foreach (var id in characterIds)
			{
				var client = new RecordingNetworkClient(id);
				client.Session.Character!.Name = $"C{id}";
				client.Session.Character.Class = ConanClass.Necromancer;
				clients[id] = client;
			}

			return (teams, dispatcher, clients);
		}

		private static Character CharacterOf(RecordingNetworkClient client)
		{
			return client.Session.Character!;
		}

		[Fact]
		public async Task Accept_FormsATeamOfTwo()
		{
			var (teams, dispatcher, clients) = Harness(1, 2);

			await teams.Invite(clients[1], CharacterOf(clients[1]), CharacterOf(clients[2]));
			await teams.AcceptInvite(2);

			var teamId = teams.TeamOf(1);
			Assert.NotNull(teamId);
			Assert.Equal(teamId, teams.TeamOf(2));
			Assert.Equal(2, teams.MembersOf(teamId!.Value).Count);

			// Both members' world connections learn about it - that is the half the CS tier does not carry.
			Assert.Contains(dispatcher.Membership, m => m is { Recipient: 1, Joined: true });
			Assert.Contains(dispatcher.Membership, m => m is { Recipient: 2, Joined: true });
		}

		[Fact]
		public async Task AcceptWithoutAnInvite_DoesNothing()
		{
			// The inviter comes from OUR pending-invite table, not the client's body, so an unsolicited accept cannot
			// pull someone into a team.
			var (teams, _, _) = Harness(1, 2);

			await teams.AcceptInvite(2);

			Assert.Null(teams.TeamOf(1));
			Assert.Null(teams.TeamOf(2));
		}

		[Fact]
		public async Task Decline_DropsThePendingInvite()
		{
			var (teams, _, clients) = Harness(1, 2);

			await teams.Invite(clients[1], CharacterOf(clients[1]), CharacterOf(clients[2]));
			teams.Decline(2);
			await teams.AcceptInvite(2);

			Assert.Null(teams.TeamOf(2));
		}

		[Fact]
		public async Task InvitingSomeoneAlreadyTeamed_IsRefused()
		{
			var (teams, _, clients) = Harness(1, 2, 3);
			await teams.Invite(clients[1], CharacterOf(clients[1]), CharacterOf(clients[2]));
			await teams.AcceptInvite(2);

			await teams.Invite(clients[3], CharacterOf(clients[3]), CharacterOf(clients[2]));
			await teams.AcceptInvite(2);

			Assert.Equal(teams.TeamOf(1), teams.TeamOf(2)); // still with the first team
			Assert.Null(teams.TeamOf(3));
		}

		// A two-member team losing one member has no team left: the last member is dropped too, which is what retail
		// does. Without this the survivor keeps a team window listing only themselves.
		[Fact]
		public async Task LeavingATeamOfTwo_DissolvesIt()
		{
			var (teams, dispatcher, clients) = Harness(1, 2);
			await teams.Invite(clients[1], CharacterOf(clients[1]), CharacterOf(clients[2]));
			await teams.AcceptInvite(2);

			await teams.Leave(2);

			Assert.Null(teams.TeamOf(1));
			Assert.Null(teams.TeamOf(2));
			Assert.Contains(dispatcher.Membership, m => m is { Member: 2, Joined: false });
			Assert.Contains(dispatcher.Membership, m => m is { Member: 1, Joined: false });
		}

		[Fact]
		public async Task LeaderLeavingAThreeManTeam_PromotesSomeoneElse()
		{
			var (teams, _, clients) = Harness(1, 2, 3);
			await teams.Invite(clients[1], CharacterOf(clients[1]), CharacterOf(clients[2]));
			await teams.AcceptInvite(2);
			await teams.Invite(clients[1], CharacterOf(clients[1]), CharacterOf(clients[3]));
			await teams.AcceptInvite(3);

			var teamId = teams.TeamOf(1)!.Value;
			Assert.Equal(3, teams.MembersOf(teamId).Count);

			await teams.Leave(1); // the leader

			Assert.Null(teams.TeamOf(1));
			Assert.Equal(2, teams.MembersOf(teamId).Count);
			Assert.Equal(teamId, teams.TeamOf(2));
			Assert.Equal(teamId, teams.TeamOf(3));
		}

		[Fact]
		public async Task OnlyTheLeaderCanKick()
		{
			var (teams, _, clients) = Harness(1, 2, 3);
			await teams.Invite(clients[1], CharacterOf(clients[1]), CharacterOf(clients[2]));
			await teams.AcceptInvite(2);
			await teams.Invite(clients[1], CharacterOf(clients[1]), CharacterOf(clients[3]));
			await teams.AcceptInvite(3);

			await teams.Kick(2, 3); // 2 is not the leader

			Assert.NotNull(teams.TeamOf(3));

			await teams.Kick(1, 3); // 1 is

			Assert.Null(teams.TeamOf(3));
		}
	}
}
