using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Core.Network.Server.CsPlayerAgent;
using Faolan.Core.Network.Server.GameServer;
using Faolan.GameServer.Progression;
using Faolan.GameServer.Quest;
using Faolan.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Faolan.Tests
{
	// Quest SHARING (#101), the DECISION layer: who gets offered the quest, who is told what, and what an accept is
	// allowed to grant.
	//
	// This is the half worth gating, because no capture performs a share - the bodies are built from the client's own
	// reader and cannot be checked against retail bytes, but the RULES they carry can be checked against the client's
	// own: the twelve feedback codes are its two jump tables, the not-shareable bit is the one that greys its Share
	// button, and the 60-unit leash is the one its share window applies to itself.
	public class QuestSharingServiceTests : System.IDisposable
	{
		// Each case builds its own throwaway database; they are deleted together at the end of the class.
		private readonly List<string> _databases = [];

		public void Dispose()
		{
			foreach (var path in _databases)
				TestDb.Delete(path);
		}

		private const uint Sharer = 0x01000001;
		private const uint Mate = 0x01000002;
		private const uint Stranger = 0x01000003;

		// A shareable quest and a non-shareable one. QuestLineFlags bit 4 (0x10) is the difference and the only one
		// that matters here; 2 and 18 are both values the corpus actually carries.
		private const uint ShareableQuest = 90001;
		private const uint LockedQuest = 90002;

		[Fact]
		public async Task Solo_IsAnsweredNotInTeam_AndOffersNobody()
		{
			var h = await Harness(roster: []);

			await h.Service.Share(Sharer, ShareableQuest);

			Assert.Equal([(Sharer, QuestShareFeedback.NotInTeam, 0u)], h.Outbound.Global);
			Assert.Empty(h.Outbound.Requests);
		}

		[Fact]
		public async Task AQuestTheSharerDoesNotHold_IsMainQuestNotFound()
		{
			var h = await Harness(roster: [Sharer, Mate]);

			await h.Service.Share(Sharer, 12345); // never granted to anyone in the harness

			Assert.Equal([(Sharer, QuestShareFeedback.MainQuestNotFound, 0u)], h.Outbound.Global);
			Assert.Empty(h.Outbound.Requests);
		}

		// The server's half of the same rule that greys the journal's Share button out.
		[Fact]
		public async Task AQuestWithTheNotShareableBit_IsMainQuestCanNotShare()
		{
			var h = await Harness(roster: [Sharer, Mate]);

			await h.Service.Share(Sharer, LockedQuest);

			Assert.Equal([(Sharer, QuestShareFeedback.MainQuestCanNotShare, 0u)], h.Outbound.Global);
			Assert.Empty(h.Outbound.Requests);
		}

		[Fact]
		public async Task AnEligibleTeamMate_IsOfferedTheQuest_AndTheSharerIsToldTheCount()
		{
			var h = await Harness(roster: [Sharer, Mate]);

			await h.Service.Share(Sharer, ShareableQuest);

			Assert.Equal([(Mate, Sharer, ShareableQuest, "Shareable")], h.Outbound.Requests);
			Assert.Equal([(Sharer, QuestShareFeedback.LegitTargets, 1u)], h.Outbound.Global);
		}

		// The 60-unit leash the client's own share window applies: past it the window would cancel itself with
		// "QuestSharePlayerMovedOutOfRange", so the offer is never made and the sharer is told how many were too far.
		[Fact]
		public async Task ATeamMateBeyondTheShareRadius_IsCountedOutOfRange_AndNotOffered()
		{
			var h = await Harness(roster: [Sharer, Mate]);
			h.Outbound.Place(Mate, x: QuestSharingService.ShareRadius + 1f);

			await h.Service.Share(Sharer, ShareableQuest);

			Assert.Empty(h.Outbound.Requests);
			Assert.Equal([(Sharer, QuestShareFeedback.OutOfRangeTargets, 1u)], h.Outbound.Global);
		}

		[Fact]
		public async Task ATeamMateOnAnotherPlayfield_IsOutOfRangeWhateverTheDistance()
		{
			var h = await Harness(roster: [Sharer, Mate]);
			h.Outbound.Place(Mate, mapId: 4242); // same coordinates, different world

			await h.Service.Share(Sharer, ShareableQuest);

			Assert.Empty(h.Outbound.Requests);
			Assert.Equal([(Sharer, QuestShareFeedback.OutOfRangeTargets, 1u)], h.Outbound.Global);
		}

		[Fact]
		public async Task ATeamMateWhoAlreadyFinishedIt_IsNamedBackToTheSharer()
		{
			var h = await Harness(roster: [Sharer, Mate]);
			await h.Database.SetCharacterQuest(Mate, ShareableQuest, QuestState.Completed);

			await h.Service.Share(Sharer, ShareableQuest);

			Assert.Equal([(Sharer, QuestShareFeedback.AlreadyFinished, "Mate")], h.Outbound.Individual);
			Assert.Equal([(Sharer, QuestShareFeedback.NoLegitTargets, 0u)], h.Outbound.Global);
			Assert.Empty(h.Outbound.Requests);
		}

		// Already on it is not a failure worth a code - the client's twelve are exhausted by the two jump tables - so
		// they are simply not a legit target and nothing individual is sent about them.
		[Fact]
		public async Task ATeamMateAlreadyOnIt_IsSilentlyNotATarget()
		{
			var h = await Harness(roster: [Sharer, Mate]);
			await h.Database.SetCharacterQuest(Mate, ShareableQuest, QuestState.Active);

			await h.Service.Share(Sharer, ShareableQuest);

			Assert.Empty(h.Outbound.Individual);
			Assert.Empty(h.Outbound.Requests);
			Assert.Equal([(Sharer, QuestShareFeedback.NoLegitTargets, 0u)], h.Outbound.Global);
		}

		[Fact]
		public async Task Accepting_GrantsTheQuestWeOffered_AndSeedsItActive()
		{
			var h = await Harness(roster: [Sharer, Mate]);
			await h.Service.Share(Sharer, ShareableQuest);

			await h.Service.Accept(Mate, ShareableQuest);

			var held = await h.Database.GetCharacterQuests(Mate);
			Assert.Equal([ShareableQuest], held.Select(q => q.QuestId));
			Assert.Equal(QuestState.Active, held[0].State);
			// The J-panel list AND the detail, in that order - the detail alone leaves the log empty.
			Assert.Equal(["log-list", "full-update"], h.QuestOutbound.Calls);
		}

		// The quest granted comes from OUR pending map, never from the body - so a client that echoes a different id
		// gets nothing rather than a quest of its choosing. The same rule the team accept and the trade start follow.
		[Fact]
		public async Task AnAcceptEchoingADifferentQuest_GrantsNothing()
		{
			var h = await Harness(roster: [Sharer, Mate]);
			await h.Service.Share(Sharer, ShareableQuest);

			await h.Service.Accept(Mate, LockedQuest);

			Assert.Empty(await h.Database.GetCharacterQuests(Mate));
		}

		[Fact]
		public async Task AnAcceptFromSomeoneWhoWasNeverOffered_GrantsNothing()
		{
			var h = await Harness(roster: [Sharer, Mate]); // Stranger is not in the team, so no offer reaches them
			await h.Service.Share(Sharer, ShareableQuest);

			await h.Service.Accept(Stranger, ShareableQuest);

			Assert.Empty(await h.Database.GetCharacterQuests(Stranger));
		}

		// The offer is one-shot: accepting consumes it, so a replayed accept cannot re-grant (and, once the state is
		// persisted, could not anyway - Grant refuses a quest already held).
		[Fact]
		public async Task DecliningAfterAccepting_TellsTheSharerNothing()
		{
			var h = await Harness(roster: [Sharer, Mate]);
			await h.Service.Share(Sharer, ShareableQuest);
			await h.Service.Accept(Mate, ShareableQuest);

			await h.Service.Decline(Mate, busy: false);

			Assert.Empty(h.Outbound.Individual);
		}

		[Theory]
		[InlineData(false, QuestShareFeedback.QuestDeclined)]
		[InlineData(true, QuestShareFeedback.PlayerBusy)]
		public async Task DecliningOrBeingBusy_NamesTheDeclinerBackToTheSharer(bool busy, QuestShareFeedback expected)
		{
			var h = await Harness(roster: [Sharer, Mate]);
			await h.Service.Share(Sharer, ShareableQuest);

			await h.Service.Decline(Mate, busy);

			Assert.Equal([(Sharer, expected, "Mate")], h.Outbound.Individual);
		}

		[Fact]
		public async Task Forgetting_ADisconnectedTarget_DropsTheirPendingOffer()
		{
			var h = await Harness(roster: [Sharer, Mate]);
			await h.Service.Share(Sharer, ShareableQuest);

			h.Service.Forget(Mate);
			await h.Service.Accept(Mate, ShareableQuest);

			Assert.Empty(await h.Database.GetCharacterQuests(Mate));
		}

		private async Task<Harnessed> Harness(uint[] roster)
		{
			var (options, path) = TestDb.NewSeededDatabase(Sharer);
			_databases.Add(path);
			await using (var ctx = new DatabaseContext(options))
			{
				foreach (var (id, name) in new[] { (Mate, "Mate"), (Stranger, "Stranger") })
					ctx.Characters.Add(new Character
					{
						Id = id, AccountId = 1, RealmId = 1, MapId = Map.TortageBeachId, Name = name,
						Class = ConanClass.Barbarian, Race = Race.Cimmerian, Sex = Sex.Male
					});

				ctx.QuestLines.Add(new QuestLine { QuestLineId = 9000 });
				// 2 and 18 are both observed @+0x14 values; they differ only in bit 4, which is the share lock.
				ctx.QuestDefinitions.Add(new QuestDefinition { QuestId = ShareableQuest, QuestLineId = 9000, Name = "Shareable", QuestLineFlags = 2 });
				ctx.QuestDefinitions.Add(new QuestDefinition { QuestId = LockedQuest, QuestLineId = 9000, Name = "Locked", QuestLineFlags = 18 });
				await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
			}

			var database = new DatabaseRepository(new Factory(options));
			await database.SetCharacterQuest(Sharer, ShareableQuest, QuestState.Active);
			await database.SetCharacterQuest(Sharer, LockedQuest, QuestState.Active);

			var outbound = new RecordingShareOutbound();
			foreach (var id in new[] { Sharer, Mate, Stranger })
				outbound.Place(id);

			var questOutbound = new RecordingQuestOutbound();
			var progression = new QuestProgressionService(database, questOutbound, null!, null!, null!, new QuestRewardService(database, questOutbound, null!, (_, _) => Task.CompletedTask, NullLogger.Instance), new DailyChallengeService(database, NullLogger.Instance), NullLogger.Instance);
			var service = new QuestSharingService(database, new FixedRoster(roster), outbound, progression, NullLogger.Instance);

			return new Harnessed(service, database, outbound, questOutbound);
		}

		private sealed record Harnessed(QuestSharingService Service, IDatabaseRepository Database, RecordingShareOutbound Outbound, RecordingQuestOutbound QuestOutbound);

		private sealed class FixedRoster(uint[] members) : ICsPlayerAgentProvider
		{
			public Task<bool> LeaveTeam(uint characterId) => Task.FromResult(false);
			public Task<bool> KickFromTeam(uint leaderId, uint targetId) => Task.FromResult(false);
			public Task<bool> NotifyMailReceived(uint recipientId, ulong mailId)
			{
				return Task.FromResult(false); // no CS tier in these tests
			}

			public Task<bool> DepositToGuildBank(uint characterId, ulong amountTin)
			{
				return Task.FromResult(false); // no guild tier in these tests
			}

			public IReadOnlyList<uint> TeamMembersOf(uint characterId) => members;

			public uint? TeamIdOf(uint characterId) => members.Contains(characterId) ? 1u : null;

			public PvpMatchWorldEntry? PvpMatchEntryOf(uint characterId, uint mapId) => null;
		}

		// Records what the sharer and the targets were told, and owns where everyone is standing.
		private sealed class RecordingShareOutbound : IQuestShareOutbound
		{
			private readonly Dictionary<uint, CharacterLiveState> _live = [];
			private readonly Dictionary<uint, INetworkClient> _clients = [];

			public List<(uint Recipient, uint Sharer, uint QuestId, string QuestName)> Requests { get; } = [];
			public List<(uint Recipient, QuestShareFeedback Type, uint Value)> Global { get; } = [];
			public List<(uint Recipient, QuestShareFeedback Type, string Name)> Individual { get; } = [];

			public void Place(uint characterId, float x = 0f, uint mapId = Map.TortageBeachId)
			{
				var name = characterId == Sharer
					? "Testchar"
					: characterId == Mate
						? "Mate"
						: "Stranger";
				_live[characterId] = new CharacterLiveState(characterId, name, characterId, mapId, 0, x, 0, 0, null, null, []);
				_clients.TryAdd(characterId, new RecordingNetworkClient(characterId));
			}

			public Task<bool> SendQuestShareRequest(uint recipientId, uint sharerId, uint questId, string questName)
			{
				Requests.Add((recipientId, sharerId, questId, questName));
				return Task.FromResult(true);
			}

			public Task SendQuestShareGlobalFeedback(uint recipientId, QuestShareFeedback type, uint value = 0)
			{
				Global.Add((recipientId, type, value));
				return Task.CompletedTask;
			}

			public Task SendQuestShareIndividualFeedback(uint recipientId, QuestShareFeedback type, string playerName)
			{
				Individual.Add((recipientId, type, playerName));
				return Task.CompletedTask;
			}

			public INetworkClient? ClientFor(uint characterId) => _clients.GetValueOrDefault(characterId);
			public CharacterLiveState? LiveStateOf(uint characterId) => _live.GetValueOrDefault(characterId);
		}

		// Only the two grant packets are exercised here; the rest of the port belongs to the supersession engine.
		private sealed class RecordingQuestOutbound : IQuestOutbound
		{
			public List<string> Calls { get; } = [];

			public Task SendQuestLogList(INetworkClient client, QuestDefinition trackedDef, IReadOnlyList<uint> activeQuestIds)
			{
				Calls.Add("log-list");
				return Task.CompletedTask;
			}

			public Task SendQuestFullUpdate(INetworkClient client, QuestDefinition def, uint? level = null)
			{
				Calls.Add("full-update");
				return Task.CompletedTask;
			}

			public Task ApplyQuestWorldEffects(INetworkClient client) => Task.CompletedTask;
			public void InvalidateReachGoals(INetworkClient client) { }
			public Task RefreshQuestMarkerFor(INetworkClient client, string npcName, uint? npcTemplateId = null) => Task.CompletedTask;
			public Task RefreshQuestVisibility(INetworkClient client) => Task.CompletedTask;

			// #528: the ambusher TYPES the engine sprang, so a gate can assert one fired without a live zone.
			public List<uint> Ambushes { get; } = [];

			public void ScheduleQuestAmbush(INetworkClient client, NpcAmbush ambush)
			{
				Ambushes.Add(ambush.NpcTemplateId);
			}

			public Task SendQuestGoal(INetworkClient client, uint questId, uint goalId, QuestGoalUpdate update) => Task.CompletedTask;
			public Task SendQuestIir(INetworkClient client, uint questId, uint state) => Task.CompletedTask;
			public Task SendQuestRewardOffer(INetworkClient client, uint questId, IReadOnlyList<QuestRewardEntry> options) => Task.CompletedTask;
			public Task SendQuestRewardConfirm(INetworkClient client, uint questId, uint index) => Task.CompletedTask;
			public bool SetNpcFollowByTemplate(uint mapId, uint templateId, uint playerHandle, bool follow) => false;
		}

		private sealed class Factory(DbContextOptions<DatabaseContext> options) : IDbContextFactory<DatabaseContext>
		{
			public DatabaseContext CreateDbContext() => new DatabaseContext(options);
			public Task<DatabaseContext> CreateDbContextAsync(System.Threading.CancellationToken ct = default) => Task.FromResult(new DatabaseContext(options));
		}
	}
}
