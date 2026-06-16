using System.IO;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.GameServer.Packets.FullCharSnapshot;
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
		private const string DbPath = "/home/melvin/Development/C#/Project Faolan/faolan.db";
		private const string TemplatePath = "/home/melvin/Development/C#/Project Faolan/Faolan/GameServer/Packets/FullCharSnapshot/BeachTailsTemplate.bin";
		private const int RegionEnd = 609;

		// The beach starter's per-quest scalars not (yet) harvested into QuestDefinition - carried from the
		// capture (docs/fullplayer-snapshot-reader.md). state=1 active; N=4; FieldEc=1; FieldF0=0x96; FieldF8=15.
		private const uint BeachState = 1, BeachN = 4, BeachFieldEc = 1, BeachFieldF0 = 0x96;
		private const ulong BeachFieldF8 = 15;
		private const uint BeachStarterQuestId = 10238;

		[Fact]
		public async Task BuildSingle_ReproducesBeachStarterRegion_ByteExact()
		{
			var def = await LoadDefinition(BeachStarterQuestId);
			Assert.NotNull(def);

			var record = QuestcRecord.BuildSingle(def, BeachState, BeachN, BeachFieldEc, BeachFieldF0, BeachFieldF8);
			var region = new QuestcRegion { Records = { record } };
			var built = region.ToArray();

			var template = File.ReadAllBytes(TemplatePath);
			Assert.Equal(RegionEnd, built.Length);
			Assert.Equal(template[..RegionEnd], built);
		}

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

		private static async Task<QuestDefinition> LoadDefinition(uint questId)
		{
			var options = new DbContextOptionsBuilder<DatabaseContext>().UseSqlite($"Data Source={DbPath}").Options;
			var repo = new DatabaseRepository(new Factory(options));
			return await repo.GetQuestDefinition(questId);
		}

		private sealed class Factory(DbContextOptions<DatabaseContext> options) : IDbContextFactory<DatabaseContext>
		{
			public DatabaseContext CreateDbContext() => new(options);
		}
	}
}
