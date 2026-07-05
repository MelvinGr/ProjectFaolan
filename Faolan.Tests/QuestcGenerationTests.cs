using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.GameServer.FullCharSnapshot;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// Proves the Quest_c region can be GENERATED (construct, don't replay) from a DB QuestDefinition rather than
	// carried verbatim in BeachTailsTemplate.bin: building the beach starter (quest 10238) from faolan.db's
	// QuestDefinition + the per-quest carried scalars reproduces the template's @0-609 region BYTE-EXACT.
	// The journal text (5 <remoteformat> strings) is fully DB-sourced; only the undecoded scalars (N, the two
	// @0xf0/@0xf8 words) are carried per-quest. Gitea #186 / #91.
	public class QuestcGenerationTests
	{
		private const int RegionEnd = 609;
		private const string DbPath = "/home/melvin/Development/C#/Project Faolan/faolan.db";
		private const string TemplatePath = "/home/melvin/Development/C#/Project Faolan/Faolan.Tests/Assets/BeachTailsTemplate.bin";
		private const uint BeachStarterQuestId = 10238;

		// The per-character quest STATE (1 = active); the only quest-portion value that isn't quest-definition data
		// (it comes from CharacterQuest live). Everything else now rides in QuestDefinition (seeded for 10238).
		private const uint BeachState = 1;

		// The @609 objective region GENERATES from the DB per-goal model (QuestGoalDefinition rows seeded for the beach
		// starter), reproducing the template's @609..1168 bytes exactly - so the whole quest region is now DB-sourced.
		[Fact]
		public async Task BuildObjectiveRegion_FromDbGoals_ReproducesBeachRegion_ByteExact()
		{
			var def = await LoadDefinition(BeachStarterQuestId);
			Assert.NotNull(def);
			Assert.Equal(2, def.Goals.Count); // the two seeded beach goals (1448 + 3490)

			const uint templateInstance = 0x02165EFF;
			var built = QuestObjectiveRegion.Build(def, templateInstance).ToArray();

			var template = File.ReadAllBytes(TemplatePath);
			Assert.Equal(template[RegionEnd..1168], built);
		}

		[Fact]
		public async Task BuildSingle_ReproducesBeachStarterRegion_ByteExact()
		{
			var def = await LoadDefinition(BeachStarterQuestId);
			Assert.NotNull(def);

			// All scalars sourced from the DB (SnapshotN/FieldEc/FieldF0/FieldF8) - no literals but the per-char state.
			var record = QuestcRecord.BuildSingle(def, BeachState, def.SnapshotN, def.SnapshotFieldEc, def.SnapshotFieldF0, def.SnapshotFieldF8);
			var region = new QuestcRegion { Records = { record } };
			var built = region.ToArray();

			var template = File.ReadAllBytes(TemplatePath);
			Assert.Equal(RegionEnd, built.Length);
			Assert.Equal(template[..RegionEnd], built);
		}

		// The production helper (gated FAOLAN_GENERATE_BEACH_QUEST_TAILS path) builds the quest portion byte-identical
		// to the beach template's @0..1292 - so flipping the flag splices identical bytes for a fresh beach char. #186.
		[Fact]
		public async Task BuildSingleQuestPortion_MatchesTemplate_ByteExact()
		{
			var def = await LoadDefinition(BeachStarterQuestId);
			Assert.NotNull(def);

			var portion = SnapshotTailsBuilder.BuildSingleQuestPortion(def, BeachState, 0x02165EFF);

			Assert.Equal(SnapshotTailsBuilder.QuestPortionLength, portion.Length);
			Assert.Equal(File.ReadAllBytes(TemplatePath)[..1292], portion);
		}

		// The WHOLE quest portion of the tails (@0..1292 = Quest_c + @609 + index) regenerates byte-exact from the DB
		// (the journal + per-goal model) plus the per-quest scalars/index data NOW SOURCED FROM THE DB - proving the
		// .bin's entire quest region authors from server state with ZERO literals (only the per-character state). #186.
		[Fact]
		public async Task BuildWholeQuestPortion_ReproducesBeachTails_ByteExact()
		{
			var def = await LoadDefinition(BeachStarterQuestId);
			Assert.NotNull(def);
			const uint instance = 0x02165EFF;

			var quests = new QuestcRegion { Records = { QuestcRecord.BuildSingle(def, BeachState, def.SnapshotN, def.SnapshotFieldEc, def.SnapshotFieldF0, def.SnapshotFieldF8) } };
			var objectives = QuestObjectiveRegion.Build(def, instance);
			var goalSteps = def.IndexGoalSteps.Split(',').Select(uint.Parse).ToArray();
			var index = QuestIndexRegion.Build([def.QuestId], goalSteps, def.IndexTableField1, def.IndexTableField2, def.IndexTableField3, def.QuestId, def.IndexTrailerWord);

			byte[] built = [.. quests.ToArray(), .. objectives.ToArray(), .. index.ToArray()];

			var template = File.ReadAllBytes(TemplatePath);
			Assert.Equal(template[..1292], built);
		}

		private static async Task<QuestDefinition> LoadDefinition(uint questId)
		{
			var options = new DbContextOptionsBuilder<DatabaseContext>().UseSqlite($"Data Source={DbPath}").Options;
			var repo = new DatabaseRepository(new Factory(options));
			return await repo.GetQuestDefinition(questId);
		}

		private sealed class Factory(DbContextOptions<DatabaseContext> options) : IDbContextFactory<DatabaseContext>
		{
			public DatabaseContext CreateDbContext()
			{
				return new DatabaseContext(options);
			}
		}
	}
}
