using System.Collections.Generic;
using System.IO;
using System.Linq;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// Byte-exact round-trip of the @609 per-goal objective-detail region (decode_snapshot_609.py,
	// docs/re/fullplayer-snapshot-reader.md). Proves QuestObjectiveRegion models the region losslessly - the
	// construct-don't-replay foundation for retiring this part of BeachTailsTemplate.bin (Gitea #186), mirroring
	// QuestcRegion for the @0..609 journal region.
	public class QuestObjectiveRegionTests
	{
		private const int RegionStart = 609, IndexTag = 1168; // Quest_c end / quest index (empirical)
		private static readonly string TemplatePath = TestPaths.Asset("BeachTailsTemplate.bin");

		private static byte[] Region => File.ReadAllBytes(TemplatePath)[RegionStart..IndexTag];

		[Fact]
		public void BuildSingle_RegeneratesTheRegion_ByteExact()
		{
			// Generation (construct, don't replay, #186): build the two beach goals from their MEANINGFUL fields
			// (goalId/n/k/subId/map/marker) - synthesizing the constants - plus their objective/trailing text records
			// (the DB-sourced data, taken here from the parsed region), and prove the assembled region reproduces the
			// template's @609 bytes exactly. Confirms a compact per-goal definition regenerates the full record.
			var parsed = QuestObjectiveRegion.Parse(File.ReadAllBytes(TemplatePath), RegionStart);
			const uint instance = 0x02165EFF; // the template's baked-in instance (re-addressed live)

			// goalIdEcho is explicit now: it is EITHER the goal id or 0 depending on the goal (1567 of 2850 captured
			// goals carry 0), so it cannot default to the id.
			var g1 = QuestObjectiveGoal.BuildSingle(instance, 10238, 1448, 1, 9, 180, 4010, 416, 701, parsed.Goals[0].Objective, parsed.Goals[0].TextRecord, goalIdEcho: 1448);
			var g2 = QuestObjectiveGoal.BuildSingle(instance, 10238, 3490, 2, 12, 0, 4010, 521, 757, parsed.Goals[1].Objective, parsed.Goals[1].TextRecord, 70099, 0x36314C56, goalIdEcho: 3490);

			var region = new QuestObjectiveRegion
			{
				Goals =
				{
					g1,
					g2
				}
			};

			Assert.Equal(Region, region.ToArray());
		}

		// Corpus invariants for the two byte-shaped goal words, recovered from QuestGoal_c::WriteToStream plus the
		// captures. Pinned so a future "these look like u32s" refactor has to argue with the data.
		[Fact]
		public void ByteShapedGoalWords_HoldTheirCorpusInvariants()
		{
			var goals = CapturedGoals().ToList();
			Assert.NotEmpty(goals);

			// Unknown1: the client writes ONE byte, so the word never exceeds 255.
			Assert.All(goals, g => Assert.InRange(g.Unknown1, 0u, 255u));

			// Unknown2: a flag, never an arbitrary number - 0 or the byte 0x01 followed by three zero bytes.
			Assert.All(goals, g => Assert.True(g.Unknown2 is 0 or QuestObjectiveGoal.FlagSet, $"Unknown2 should be a flag but goal {g.GoalId} carries 0x{g.Unknown2:X8}"));
		}

		[Fact]
		public void Parse_DecodesTheTwoBeachGoals()
		{
			var region = QuestObjectiveRegion.Parse(File.ReadAllBytes(TemplatePath), RegionStart);

			Assert.Equal(2, region.Goals.Count);

			var g1 = region.Goals[0];
			Assert.Equal(10238u, g1.QuestId);
			Assert.Equal(1448u, g1.GoalId);
			Assert.Equal((416u, 701u), (g1.MarkerX, g1.MarkerY)); // matches QuestDefinition's marker
			Assert.Contains("category=\"50304\"", g1.Objective);
			Assert.All(g1.Reserved, w => Assert.Equal(0u, w));

			Assert.Equal(1u, g1.RequiredCount); // beach objectives are "do 1" (the on-wire count, #47)

			var g2 = region.Goals[1];
			Assert.Equal(10238u, g2.QuestId);
			Assert.Equal(3490u, g2.GoalId);
			Assert.Equal((521u, 757u), (g2.MarkerX, g2.MarkerY));
			Assert.Equal(1u, g2.RequiredCount);
			Assert.Contains("<localized", g2.TextRecord);
		}

		[Fact]
		public void Parse_ThenToArray_RoundTripsByteExact()
		{
			var template = File.ReadAllBytes(TemplatePath);

			var region = QuestObjectiveRegion.Parse(template, RegionStart, out var consumed);

			Assert.Equal(IndexTag - RegionStart, consumed); // consumed exactly to the index boundary
			Assert.Equal(Region, region.ToArray());
		}

		[Fact]
		public void RequiredCount_GreaterThanOne_RoundTripsThroughTheWire()
		{
			// The goal record's count field (the word after goalIdRepeat) is NOT a constant 1: values 1/3/4/8/12/64 are
			// seen across captures (e.g. Hoxxling quest 14012 = 3, quest 13778 goal 6658 = 64), so it is the objective's
			// required amount (#47). Prove a count > 1 survives BuildSingle -> Write -> Read.
			var obj = "<remoteformat id=\"6491\" category=\"50304\" key=\"x\" >";
			var txt = "<remoteformat id=\"1\" category=\"51200\" key=\"y\" >";
			var goal = QuestObjectiveGoal.BuildSingle(0x1234, 14012, 6491, 1, 12, 0, 4010, 0, 0, obj, txt, requiredCount: 3);
			Assert.Equal(3u, goal.RequiredCount);

			var region = new QuestObjectiveRegion
			{
				Goals =
				{
					goal
				}
			};
			var reparsed = QuestObjectiveRegion.Parse(region.ToArray(), 0);
			Assert.Equal(3u, reparsed.Goals[0].RequiredCount);
		}

		private static IEnumerable<QuestObjectiveGoal> CapturedGoals()
		{
			foreach (var (_, subClass, _) in CompletedQuestJournalTests.SubClassesForProbe())
			foreach (var record in subClass.Quests.Records)
			{
				if (record.Objectives is not { Goals.Count: > 0 } objectives)
					continue;

				foreach (var goal in objectives.Goals)
					yield return goal;
			}
		}
	}
}
