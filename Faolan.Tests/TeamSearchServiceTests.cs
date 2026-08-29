using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Enums;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Team;
using Faolan.Core.Network.Codecs.Text;
using Faolan.Core.Network.Server.ChatServer;
using Faolan.Core.Network.Server.GameServer;
using Faolan.CSPlayerAgent;
using Faolan.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Faolan.Tests
{
	// The LFG registry behind DispatchTeamSearchResult (#889): which teams a SearchForTeam answers with, and which of
	// the client's criteria actually bite.
	//
	// The wire grammar is gated separately (TeamSearchResultCodecTests); this is the half the corpus cannot show at
	// all, because every captured search matched nothing - so what is asserted here is the RULE, stated once in
	// TeamService.SearchTeams: a team appears when its leader advertised it and every criterion the client sent
	// admits it.
	public class TeamSearchServiceTests
	{
		private const uint Leader = 0x01000001;
		private const uint Member = 0x01000002;
		private const uint Searcher = 0x01000003;
		private const uint Outsider = 0x01000004;

		private const uint Tortage = 4020;
		private const uint Khemi = 4030;

		// A team of two in Tortage, advertised, plus a searcher who is not in it. Returns the service and the team id.
		private static async Task<(TeamService Teams, uint TeamId)> AdvertisedTeam(string message = "Need a healer")
		{
			var teams = Service();
			await teams.Invite(Client(Leader), Character(Leader), Character(Member));
			await teams.AcceptInvite(Member);

			var teamId = teams.TeamOf(Leader)!.Value;
			Assert.NotNull(teams.SetLookingForMembers(Leader, true, message));

			return (teams, teamId);
		}

		private static readonly Dictionary<uint, RecordingNetworkClient> Clients = [];

		private static RecordingNetworkClient Client(uint characterId)
		{
			return Clients[characterId];
		}

		private static Character Character(uint characterId)
		{
			return Client(characterId).Session.Character!;
		}

		// Four online characters: the team's two in Tortage, a searcher in Tortage, and one in another realm.
		private static TeamService Service()
		{
			Clients.Clear();
			foreach (var (id, level, mapId, realmId) in new[] { (Leader, 40u, Tortage, 1u), (Member, 20u, Tortage, 1u), (Searcher, 30u, Tortage, 1u), (Outsider, 30u, Khemi, 2u) })
			{
				var client = new RecordingNetworkClient(id);
				client.Session.Character!.Level = (ushort)level;
				client.Session.Character!.MapId = mapId;
				client.Session.Character!.RealmId = realmId;
				client.Session.Character!.Name = $"Char{id:X8}";
				Clients[id] = client;
			}

			return new TeamService(NullLogger.Instance, new NoWorldDispatcher(), new NoOpChatDispatcher(), id => Clients.GetValueOrDefault(id));
		}

		// The wide-open search: no playfields, the whole level range, any size. This is the shape of the captured
		// requests (levels 1..10 / 1..80, member counts 1..5).
		private static TeamSearchResult Search(TeamService teams, uint minLevel = 1, uint maxLevel = 80, uint memberCountMin = 1, uint memberCountMax = 5, uint skip = 0,
			IReadOnlyList<uint>? playfields = null)
		{
			return teams.SearchTeams(Searcher, playfields ?? [], minLevel, maxLevel, memberCountMin, memberCountMax, skip);
		}

		[Fact]
		public async Task AnAdvertisedTeam_IsReturnedWithItsRosterAndMessage()
		{
			var (teams, teamId) = await AdvertisedTeam();

			var result = Search(teams);

			var row = Assert.Single(result.Rows);
			Assert.Equal(teamId, row.Team.TeamId);
			Assert.Equal(Leader, row.Team.LeaderId);
			Assert.Equal(2, row.Team.Members.Count);
			Assert.Equal("Need a healer", row.Message);
			Assert.False(result.MoreHits);
		}

		// The point of the flag: a team that never advertised is not in anyone's search results. This is what makes
		// the empty body the RIGHT answer on a server where nobody has flagged, rather than a missing feature.
		[Fact]
		public async Task AnUnadvertisedTeam_IsNotReturned()
		{
			var (teams, _) = await AdvertisedTeam();

			Assert.NotNull(teams.SetLookingForMembers(Leader, false, ""));

			Assert.Empty(Search(teams).Rows);
		}

		[Fact]
		public async Task OnlyTheLeaderMayAdvertise()
		{
			var (teams, _) = await AdvertisedTeam();

			Assert.Null(teams.SetLookingForMembers(Member, false, "not mine to set"));
			Assert.Single(Search(teams).Rows); // still advertised, with the leader's own message

			Assert.Null(teams.SetLookingForMembers(Searcher, true, "in no team at all"));
		}

		// Every criterion the client sends is applied. Each case here is one field of the captured 24-byte request.
		[Fact]
		public async Task TheLevelRange_KeepsATeamWithAnyMemberInside()
		{
			var (teams, _) = await AdvertisedTeam();

			Assert.Single(Search(teams, minLevel: 35, maxLevel: 45).Rows); // the leader, at 40
			Assert.Single(Search(teams, minLevel: 15, maxLevel: 25).Rows); // the other member, at 20
			Assert.Empty(Search(teams, minLevel: 50, maxLevel: 80).Rows); // neither
		}

		[Fact]
		public async Task TheMemberCountRange_IsTheRosterSize()
		{
			var (teams, _) = await AdvertisedTeam();

			Assert.Single(Search(teams, memberCountMin: 2, memberCountMax: 5).Rows);
			Assert.Empty(Search(teams, memberCountMin: 3, memberCountMax: 5).Rows);
			Assert.Empty(Search(teams, memberCountMin: 1, memberCountMax: 1).Rows);
		}

		[Fact]
		public async Task ThePlayfieldList_KeepsATeamWithAMemberInOneOfThem()
		{
			var (teams, _) = await AdvertisedTeam();

			Assert.Single(Search(teams, playfields: [Tortage]).Rows);
			Assert.Single(Search(teams, playfields: [Khemi, Tortage]).Rows);
			Assert.Empty(Search(teams, playfields: [Khemi]).Rows);
		}

		// i_nSkipResults + i_bMoreHits: the client pages, and is told when it has not seen everything.
		[Fact]
		public async Task Paging_SkipsAndReportsMoreHits()
		{
			var (teams, _) = await AdvertisedTeam();

			Assert.Empty(Search(teams, skip: 1).Rows);
			Assert.False(Search(teams, skip: 1).MoreHits);
			Assert.False(Search(teams).MoreHits); // one match, one page - nothing left behind

			// The skip is a client-supplied u32: an out-of-range one must page PAST the end, not wrap negative and
			// hand back everything.
			Assert.Empty(Search(teams, skip: uint.MaxValue).Rows);
			Assert.False(Search(teams, skip: uint.MaxValue).MoreHits);
		}

		[Fact]
		public async Task TheCharactersOwnLookingForTeamFlag_IsHeldSeparately()
		{
			var teams = Service();

			Assert.False(teams.LookingForTeamStatusOf(Searcher).Looking);

			var set = teams.SetLookingForTeam(Searcher, true, "healer lfg");
			Assert.True(set.Looking);
			Assert.Equal("healer lfg", teams.LookingForTeamStatusOf(Searcher).Message);

			// It is the CHARACTER's flag, not a team advert - it puts nothing in the team search.
			Assert.Empty(Search(teams).Rows);

			teams.ForgetLookingForTeam(Searcher);
			Assert.False(teams.LookingForTeamStatusOf(Searcher).Looking);
		}

		// The apply button on a result row tells the LEADER, and nobody else.
		[Fact]
		public async Task Apply_TellsTheLeader()
		{
			var (teams, teamId) = await AdvertisedTeam();
			Client(Leader).Sent.Clear();
			Client(Member).Sent.Clear();

			Assert.True(await teams.Apply(Searcher, teamId));

			Assert.NotEmpty(Client(Leader).Sent);
			Assert.Empty(Client(Member).Sent);

			// The body is the applicant's own member record, so the leader's window can show who applied.
			Assert.Contains(Convert.ToHexStringLower(TeamMemberRecordCodec.Build(teams.MemberRecordOf(Searcher)!)), Client(Leader).SentHex());
		}

		[Fact]
		public async Task Apply_ToATeamThatDoesNotExist_IsRefused()
		{
			var (teams, _) = await AdvertisedTeam();

			Assert.False(await teams.Apply(Searcher, 0xDEADBEEF));
		}

		private sealed class NoOpChatDispatcher : IChatServerDispatcher
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

		private sealed class NoWorldDispatcher : IGameServerDispatcher
		{
			public Task<bool> TeleportCharacter(uint characterId, uint mapId, Vector3? position = null) => Task.FromResult(true);
			public Task<bool> SendGroupMembership(uint recipientId, uint memberId, uint teamId, uint leaderId, bool joined) => Task.FromResult(true);
			public Task<bool> CreditCharacterCash(uint characterId, ulong amountTin) => Task.FromResult(false);
			public bool DisconnectCharacter(uint characterId) => false;
			public CharacterLiveState? GetCharacterLiveState(uint characterId) => null;
			public IEnumerable<INetworkClientSession> GetWorldSessions() => [];
			public IReadOnlyList<ZoneEntityInfo> GetZoneEntities(uint mapId) => [];
			public Task<bool> RunGmCommand(uint characterId, string commandText) => Task.FromResult(false);
			public Task<bool> SendGuildTag(uint characterId, string guildName) => Task.FromResult(false);
			public Task<bool> SendMinigameMatchEnded(uint characterId, uint winningSide, MinigameResult yourResult) => Task.FromResult(false);
			public Task<bool> SendMinigameForceStop(uint characterId, string message) => Task.FromResult(false);
			public void Register(IGameServerProvider provider) { }
			public void Unregister(IGameServerProvider provider) { }
		}
	}
}
