using System.IO;
using System.Text;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// Locks the decoded grammar of the 0x737A6DF9 tails' @609 OBJECTIVE-DETAIL region (between the Quest_c
	// journal region @0..609 and the quest index @1168). Decode + dissector: tools/capture/decode_snapshot_609.py;
	// docs/re/fullplayer-snapshot-reader.md.
	//
	// KEY CORRECTION (2026-06-16): @609 is NOT a "QuestFullUpdate block authored by BuildQuestFullUpdate" (the
	// handover's earlier side-thread). It is its own per-GOAL objective-detail grammar: [0x0BD3 chapterTag] then
	// per goal [1][k][instance][questId][goalId]...[marker]...[u32 objLen][<remoteformat 50304>][text record].
	// The beach starter has TWO goals (1448 + 3490), each with its own world marker - richer than QuestDefinition's
	// single GoalId. Generating this region therefore needs a per-goal definition model (Gitea #186).
	public class Snapshot609AnalysisTests
	{
		private const int RegionStart = 609, IndexTag = 1168; // empirical boundaries (Quest_c end / quest index)
		private static readonly string DbPath = TestPaths.FaolanDb;
		private static readonly string TemplatePath = TestPaths.Asset("BeachTailsTemplate.bin");

		private static byte[] Template => File.ReadAllBytes(TemplatePath);

		[Fact]
		public async Task BuildQuestFullUpdate_DoesNotReproduceRegion609()
		{
			// The previously-assumed authoring path produces a 869 B block that starts [questId][1][questId][3]...,
			// whereas the template region is 559 B starting [0x0BD3][1][9][instance]... - a different grammar.
			var def = await LoadDefinition(10238);
			var iir = QuestFullUpdates.ForQuest(0x02165EFF, def, QuestFullUpdateCodec.DefaultQuestLevel);
			var full = QuestFullUpdateCodec.Build(iir);

			// The full-update's payload IS the quest's journal record (#530 wave 5), so the "content block" the older
			// slice `full[17..^1]` cut out is just that record's own bytes.
			var block = QuestcRecordCodec.Build(iir.Record);

			var region = Template[RegionStart..IndexTag];
			Assert.NotEqual(region.Length, block.Length); // 559 vs 869
			Assert.NotEqual(region[2], block[2]); // 0x0B (bd3) vs 0x27 (questId) - different grammars
		}

		[Fact]
		public void Region609_IsAGoalDetailBlock_NotAQuestFullUpdateBlock()
		{
			var t = Template;

			// It opens with the 0x00000BD3 chapter tag, NOT a Quest_c/QuestFullUpdate header (which would open with
			// the questId), and the first goal carries questId 10238 + goalId 1448 right after the [1][k] separator.
			Assert.Equal(0x00000BD3u, t.ReadU32Be(RegionStart)); // chapterTag
			Assert.Equal(1u, t.ReadU32Be(RegionStart + 4)); // [1]
			Assert.Equal(0x02165EFFu, t.ReadU32Be(RegionStart + 12)); // instance (template's baked-in)
			Assert.Equal(10238u, t.ReadU32Be(RegionStart + 16)); // questId
			Assert.Equal(1448u, t.ReadU32Be(RegionStart + 20)); // goal 1 id
		}

		[Fact]
		public void Region609_ObjectiveText_IsDbSourced_ForBothGoals()
		{
			// Both goals' objective <remoteformat category="50304"> records appear in the region with the DB's keys,
			// proving the journal TEXT is DB-sourced (only the per-goal structure/markers are not yet in the model).
			var region = Encoding.Latin1.GetString(Template[RegionStart..IndexTag]);
			Assert.Contains("category=\"50304\"", region);
			Assert.Contains("id=\"1448\"", region); // goal 1 objective
			Assert.Contains("id=\"3490\"", region); // goal 2 objective
		}

		private static async Task<QuestDefinition> LoadDefinition(uint questId)
		{
			var options = TestDb.Options(DbPath);
			var repo = new DatabaseRepository(new Factory(options));
			return (await repo.GetQuestDefinition(questId))!;
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
