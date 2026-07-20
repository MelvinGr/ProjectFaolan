using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Faolan.Tests
{
	// CONSTRUCT, don't replay: rebuild captured completed-quest journal regions from DB rows and compare byte-for-byte
	// against the capture (#390). This is the gate that matters for generation - the parse round-trip only proves the
	// codec agrees with itself, whereas this proves the harvested QuestDefinitions/QuestAreas actually reproduce the
	// wire.
	//
	// Ordering is an INPUT, not stored state: the wire order is per-character and not derivable, and the client
	// re-sorts for display anyway, so the test feeds each capture's own order and checks the CONTENT.
	public class CompletedQuestJournalGenerationTests(ITestOutputHelper o)
	{
		// The completed journal is only emitted ALONGSIDE a non-empty active journal, because that is the only shape
		// the corpus shows: 81 captured completed journals, every one paired with active quests, and 0 counter-
		// examples in 104 captures.
		//
		// This is pinned as a test because emitting the other shape looks like an obvious improvement - a character
		// with 1511 completed and 0 eligible active quests plainly "should" get its Completed tab - and doing it
		// crashed the client on statlist.cpp:134. Byte-exact generation proves the bytes; it says nothing about
		// whether the client accepts the SHAPE they sit in.
		[Fact]
		public void CompletedJournal_IsNotEmitted_WithoutActiveQuests()
		{
			var area = new QuestArea { AreaId = 241, Magic = 0x57365154, AreaTitleKey = "k", MapKey = "k", RegionKey = "k" };
			var quest = new QuestDefinition { QuestId = 10238, AreaId = 241, JournalSequence = 1 };
			var groups = new List<(QuestArea, IReadOnlyList<QuestDefinition>)> { (area, [quest]) };

			Assert.Equal(FullCharSnapshotSubClass.BuildEmptyQuestPortion(),
				FullCharSnapshotSubClass.BuildQuestPortion([], null, 1, groups));
		}

		// Can the quest INDEX block be DERIVED, instead of harvested? It is the last thing blocking an active quest
		// from riding the snapshot (#389). Two candidates: its Magic is the quest's AREA 4CC, and its GoalSteps are
		// the quest's own @609 objective goal ids.
		[Fact]
		public async Task QuestIndexBlocks_AreDerivableFromAreaAndGoals()
		{
			await using var db = new DatabaseContext(TestDb.Options(TestPaths.FaolanDb));
			var defs = await db.QuestDefinitions.ToDictionaryAsync(q => q.QuestId);
			var areas = await db.QuestAreas.ToDictionaryAsync(a => a.AreaId);

			int blocks = 0, magicMatchesArea = 0, magicKnown = 0, stepsMatchGoals = 0, stepsKnown = 0;
			var samples = new List<string>();
			var dumped = 6;

			foreach (var (source, sub, _) in CompletedQuestJournalTests.SubClassesForProbe())
			{
				if (sub.QuestIndex is not { } idx)
					continue;

				foreach (var b in idx.Blocks)
				{
					blocks++;
					if (!defs.TryGetValue(b.TrailerQuestId, out var def))
						continue;

					if (areas.TryGetValue(def.AreaId, out var area) && area.Magic != 0)
					{
						magicKnown++;
						if (area.Magic == b.Magic)
							magicMatchesArea++;
						else if (samples.Count < 5)
							samples.Add($"quest {b.TrailerQuestId} area {def.AreaId}: block magic 0x{b.Magic:X8} vs area 0x{area.Magic:X8}");
					}

					// The quest's @609 goal ids, from the SAME snapshot's journal record.
					var rec = sub.Quests.Records.FirstOrDefault(r => r.QuestId == b.TrailerQuestId);
					if (rec?.Objectives is { Goals.Count: > 0 } obj)
					{
						stepsKnown++;
						if (obj.Goals.Select(g => g.GoalId).SequenceEqual(b.AreaQuestIds))
							stepsMatchGoals++;
						else if (samples.Count < 8)
							samples.Add($"quest {b.TrailerQuestId}: steps [{string.Join(",", b.AreaQuestIds)}] vs goals [{string.Join(",", obj.Goals.Select(g => g.GoalId))}]");
					}
				}
			}

			// Structure: what do the blocks and the top-level id list correspond to?
			foreach (var (source, sub, _) in CompletedQuestJournalTests.SubClassesForProbe())
			{
				if (sub.QuestIndex is not { } ix || sub.Quests.Records.Count == 0)
					continue;

				var activeIds = sub.Quests.Records.Select(r => r.QuestId).ToList();
				var activeAreas = activeIds.Select(q => defs.TryGetValue(q, out var d) ? d.AreaId : 0).Distinct().ToList();
				o.WriteLine($"  {source[..Math.Min(40, source.Length)]}: active={activeIds.Count} activeAreas={activeAreas.Count} " +
					$"blocks={ix.Blocks.Count} indexIds=[{string.Join(",", ix.QuestIds)}] trailers=[{string.Join(",", ix.Blocks.Select(b => b.TrailerQuestId))}] " +
					$"activeIds=[{string.Join(",", activeIds)}]");
				if (--dumped <= 0)
					break;
			}

			o.WriteLine($"index blocks={blocks}");
			o.WriteLine($"  Magic == area 4CC:      {magicMatchesArea} of {magicKnown} comparable");
			o.WriteLine($"  GoalSteps == goal ids:  {stepsMatchGoals} of {stepsKnown} comparable");
			foreach (var x in samples)
				o.WriteLine($"    {x}");
		}

		[Fact]
		public async Task CapturedRegions_RebuildFromTheDatabase_ByteExact()
		{
			await using var db = new DatabaseContext(TestDb.Options(TestPaths.FaolanDb));

			var defs = await db.QuestDefinitions.Include(q => q.TextRefs).ToDictionaryAsync(q => q.QuestId);
			var areas = await db.QuestAreas.ToDictionaryAsync(a => a.AreaId);
			o.WriteLine($"catalog: {defs.Count} quest definitions, {areas.Count} areas");

			int regions = 0, rebuilt = 0, skippedUnknown = 0, mismatched = 0;
			var firstMismatch = "";

			foreach (var (source, region) in CompletedQuestJournalTests.CapturedRegions())
			{
				regions++;

				// Only regions whose every area AND quest we actually hold can be rebuilt; anything else would be
				// testing the harvest's coverage rather than the builder.
				var groups = new List<(QuestArea, IReadOnlyList<QuestDefinition>)>();
				var complete = true;
				foreach (var g in region.Areas)
				{
					if (!areas.TryGetValue(g.AreaId, out var area) || !g.Quests.All(q => defs.ContainsKey(q.QuestId)))
					{
						complete = false;
						break;
					}

					groups.Add((area, [.. g.Quests.Select(q => defs[q.QuestId])]));
				}

				if (!complete)
				{
					skippedUnknown++;
					continue;
				}

				var built = CompletedQuestJournalRegion.Build(groups).ToArray();
				var expected = region.ToArray();
				if (built.SequenceEqual(expected))
				{
					rebuilt++;
					continue;
				}

				mismatched++;
				if (firstMismatch.Length > 0)
					continue;

				// Report the first differing AREA, not just a byte offset - the byte offset says nothing about which
				// field is wrong, and every failure on the way here was a single field (map/region order, then the
				// per-quest sequence word).
				for (var a = 0; a < region.Areas.Count; a++)
				{
					var cap = new CompletedQuestJournalRegion { Areas = [region.Areas[a]] }.ToArray();
					var got = CompletedQuestJournalRegion.Build([groups[a]]).ToArray();
					if (cap.SequenceEqual(got))
						continue;

					var g = region.Areas[a];
					firstMismatch = $"{source}: area[{a}] id={g.AreaId} quests={g.Quests.Count} " +
						$"magic=0x{g.Magic:X8} seqs=[{string.Join(",", g.Quests.Take(5).Select(q => q.Sequence))}] " +
						$"({cap.Length} B captured vs {got.Length} B built)";
					break;
				}
			}

			o.WriteLine($"regions={regions} rebuiltByteExact={rebuilt} mismatched={mismatched} skipped(catalog gaps)={skippedUnknown}");
			if (firstMismatch.Length > 0)
				o.WriteLine($"  {firstMismatch}");

			Assert.True(rebuilt > 0, "no captured region could be rebuilt from the database");
			Assert.Equal(0, mismatched);
		}
	}
}
