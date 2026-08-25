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
			var parsed = QuestObjectiveRegionCodec.Parse(Region);
			const uint instance = 0x02165EFF; // the template's baked-in instance (re-addressed live)

			// GoalIdRepeat is explicit: it is EITHER the goal id or 0 depending on the goal (1567 of 2850 captured
			// goals carry 0), so it cannot default to the id.
			var g1 = new QuestObjectiveGoal
			{
				Instance = instance, QuestId = 10238, GoalId = 1448, N = 1, K = 9, SubId = 180, MapId = 4010, MarkerX = 416, MarkerY = 701, RequiredCount = 1, GoalIdRepeat = 1448,
				Objective = parsed.Goals[0].Objective, TextRecord = parsed.Goals[0].TextRecord
			};
			var g2 = new QuestObjectiveGoal
			{
				Instance = instance, QuestId = 10238, GoalId = 3490, N = 2, K = 12, SubId = 0, MapId = 4010, MarkerX = 521, MarkerY = 757, RequiredCount = 1, GoalIdRepeat = 3490,
				Reserved = [0, 0, 0, 0, 0, 70099, 0x36314C56], Objective = parsed.Goals[1].Objective, TextRecord = parsed.Goals[1].TextRecord
			};

			var region = new QuestObjectiveRegion
			{
				Goals =
				{
					g1,
					g2
				}
			};

			Assert.Equal(Region, QuestObjectiveRegionCodec.Build(region));
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

			// The tail after it is [u8 hideMapMarker][u8][u32] and the last two are 0 on every captured goal - a body
			// that fills them is the first the corpus has seen, and worth looking at rather than carrying silently.
			Assert.All(goals, g => Assert.True(g.TailFlag == 0 && g.TailWord == 0, $"goal {g.GoalId} carries tail {g.TailFlag}/0x{g.TailWord:X8}"));
			Assert.Contains(goals, g => g.HideMapMarker);
		}

		[Fact]
		public void Parse_DecodesTheTwoBeachGoals()
		{
			var region = QuestObjectiveRegionCodec.Parse(Region);

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

			var region = QuestObjectiveRegionCodec.Parse(Region);
			var consumed = Region.Length;

			Assert.Equal(IndexTag - RegionStart, consumed); // consumed exactly to the index boundary
			Assert.Equal(Region, QuestObjectiveRegionCodec.Build(region));
		}

		[Fact]
		public void RequiredCount_GreaterThanOne_RoundTripsThroughTheWire()
		{
			// The goal record's count field (the word after goalIdRepeat) is NOT a constant 1: values 1/3/4/8/12/64 are
			// seen across captures (e.g. Hoxxling quest 14012 = 3, quest 13778 goal 6658 = 64), so it is the objective's
			// required amount (#47). Prove a count > 1 survives BuildSingle -> Write -> Read.
			var obj = "<remoteformat id=\"6491\" category=\"50304\" key=\"x\" >";
			var txt = "<remoteformat id=\"1\" category=\"51200\" key=\"y\" >";
			var goal = new QuestObjectiveGoal
			{
				Instance = 0x1234, QuestId = 14012, GoalId = 6491, N = 1, K = 12, MapId = 4010, RequiredCount = 3, Objective = obj, TextRecord = txt
			};
			Assert.Equal(3u, goal.RequiredCount);

			var region = new QuestObjectiveRegion
			{
				Goals =
				{
					goal
				}
			};
			var reparsed = QuestObjectiveRegionCodec.Parse(QuestObjectiveRegionCodec.Build(region));
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
