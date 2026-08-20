using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Enums;
using Faolan.GameServer.Progression;
using Faolan.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Faolan.Tests
{
	// Daily challenges (#509). The model is the client's - twelve stats, five category caps, three meta caps - and
	// the two behaviours gated here are the two the CORPUS states:
	//
	//   * a cap is a THRESHOLD, not a clamp (a captured character sits at DailyQuestsCompleted 7 against a cap of 3),
	//     so the challenge completes on the step that CROSSES it and not again afterwards;
	//   * a completion moves four counters together in one push - the three metas and the lifetime tally.
	//
	// Plus the reset, which is the part with no wire evidence at all and therefore the part most worth pinning
	// against its own rules rather than against a capture.
	public class DailyChallengeServiceTests : IDisposable
	{
		private const uint Character = 0x01000001;

		private readonly List<string> _databases = [];

		public void Dispose()
		{
			foreach (var path in _databases)
				TestDb.Delete(path);
		}

		[Fact]
		public void TheModelIsTheClients_CapsAndCountersAgree()
		{
			// The five categories and their shipped caps (RDB 1000516), and the three meta tiers whose caps are the
			// client's own milestone strings "Completed 3 / 15 / 60 Daily Challenges".
			Assert.Equal(5, DailyChallengeService.Categories.Count);
			Assert.Equal(3, DailyChallengeService.Metas.Count);

			Assert.Equal([3, 15, 60], DailyChallengeService.Metas.Select(m => m.Cap));
			Assert.Equal(3, DailyChallengeService.Categories.First(c => c.Counter == CharacterStatId.DailyQuestsCompleted).Cap);
			Assert.Equal(20, DailyChallengeService.Categories.First(c => c.Counter == CharacterStatId.DailyPvPKills).Cap);

			// Every counter is a distinct stat - a duplicate would make one category silently shadow another.
			var all = DailyChallengeService.Categories.Select(c => c.Counter).Concat(DailyChallengeService.Metas.Select(m => m.Counter)).ToArray();
			Assert.Equal(all.Length, all.Distinct().Count());
		}

		[Fact]
		public async Task AQuestLineCompletion_TicksTheQuestCounter_AndNothingElseYet()
		{
			var h = await Harness();
			var now = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

			await h.Service.RecordQuestLineCompleted(h.Client, Character, now);
			var changed = await ReadSheet(h.Database);

			// One line ended: the category counter is at 1, well under its cap of 3, so no challenge completed and
			// the meta counters have not moved.
			Assert.Equal(1u, changed[CharacterStatId.DailyQuestsCompleted]);
			Assert.False(changed.ContainsKey(CharacterStatId.DailyDailyCounter));
			Assert.False(changed.ContainsKey(CharacterStatId.CounterDailyChallenges));
		}

		[Fact]
		public async Task TheChallengeCompletesOnTheStepThatCROSSESTheCap_AndTheFourCountersMoveTogether()
		{
			var h = await Harness();
			var now = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

			// Three line completions = the DailyQuestsCompletedCap of 3.
			for (var i = 0; i < 3; i++)
				await h.Service.RecordQuestLineCompleted(h.Client, Character, now);

			var sheet = await ReadSheet(h.Database);
			Assert.Equal(3u, sheet[CharacterStatId.DailyQuestsCompleted]);

			// ⭐ The lockstep the capture shows: all four at once, all at 1.
			Assert.Equal(1u, sheet[CharacterStatId.DailyDailyCounter]);
			Assert.Equal(1u, sheet[CharacterStatId.DailyWeeklyCounter]);
			Assert.Equal(1u, sheet[CharacterStatId.DailyMonthlyCounter]);
			Assert.Equal(1u, sheet[CharacterStatId.CounterDailyChallenges]);

			// ⚠️ A fourth completion must NOT complete the challenge again - the counter runs past its cap (retail's
			// own characters do), so a `>=` test would re-award on every further quest for the rest of the day.
			await h.Service.RecordQuestLineCompleted(h.Client, Character, now);

			sheet = await ReadSheet(h.Database);
			Assert.Equal(4u, sheet[CharacterStatId.DailyQuestsCompleted]);
			Assert.Equal(1u, sheet[CharacterStatId.DailyDailyCounter]);
			Assert.Equal(1u, sheet[CharacterStatId.CounterDailyChallenges]);
		}

		[Fact]
		public async Task TheDailyRollover_ClearsTheCategoriesAndTheDailyMeta_ButNotTheWeeklyOrTheLifetimeTally()
		{
			var h = await Harness();
			var day1 = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

			for (var i = 0; i < 3; i++)
				await h.Service.RecordQuestLineCompleted(h.Client, Character, day1);

			// The next day: the daily bucket rolls, the weekly one does not.
			await h.Service.RecordQuestLineCompleted(h.Client, Character, day1.AddDays(1).AddMinutes(1));

			var sheet = await ReadSheet(h.Database);

			// Cleared and then stepped by this completion, so 1 rather than 4.
			Assert.Equal(1u, sheet[CharacterStatId.DailyQuestsCompleted]);
			Assert.Equal(0u, sheet[CharacterStatId.DailyDailyCounter]);

			// The weekly/monthly tallies count CHALLENGES, not activity, so a daily rollover leaves them alone - and
			// the lifetime counter is never cleared by anything (the achievement ladder counts it to 500).
			Assert.Equal(1u, sheet[CharacterStatId.DailyWeeklyCounter]);
			Assert.Equal(1u, sheet[CharacterStatId.DailyMonthlyCounter]);
			Assert.Equal(1u, sheet[CharacterStatId.CounterDailyChallenges]);
		}

		[Fact]
		public async Task AFirstEverCompletion_StampsTheResetInstantsWithoutClearingAnything()
		{
			// The guard that matters on a fresh character: with no stamp stored, "last reset was at 0" would look like
			// an overdue rollover and clear the counters the snapshot had just restored.
			var h = await Harness();
			var now = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

			await h.Service.RecordQuestLineCompleted(h.Client, Character, now);

			var sheet = await ReadSheet(h.Database);
			Assert.Equal(1u, sheet[CharacterStatId.DailyQuestsCompleted]);
			foreach (var (_, _, stamp, _) in DailyChallengeService.Metas)
				Assert.Equal((uint)now.ToUnixTimeSeconds(), sheet[stamp]);
		}

		[Fact]
		public async Task ANonCategoryStat_IsRefused()
		{
			var h = await Harness();

			await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
				h.Service.RecordCategoryProgress(h.Client, Character, CharacterStatId.DailyDailyCounter, DateTimeOffset.UnixEpoch));
		}

		private static async Task<Dictionary<CharacterStatId, uint>> ReadSheet(ICharacterRepository database)
		{
			return (await database.GetCharacterStats(Character)).GroupBy(s => s.StatId).ToDictionary(g => g.Key, g => g.First().Value);
		}

		private sealed record Harnessed(DailyChallengeService Service, ICharacterRepository Database, RecordingNetworkClient Client);

		private async Task<Harnessed> Harness()
		{
			var (options, path) = TestDb.NewSeededDatabase(Character);
			_databases.Add(path);

			var database = new DatabaseRepository(new TestDb.ContextFactory(options));
			await Task.CompletedTask;

			return new Harnessed(new DailyChallengeService(database, NullLogger.Instance), database, new RecordingNetworkClient(Character));
		}
	}
}
