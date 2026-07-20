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
		// REGRESSION: the whole suite stayed green while both journal tabs rendered EMPTY in-client, because every
		// test built a portion for a character that HAD active snapshot quests. BuildQuestPortion returned the empty
		// region early when there were none, silently dropping the completed journal with it - and a character with 0
		// eligible active quests and 1500 completed ones is the normal case right now.
		[Fact]
		public void CompletedJournal_IsEmitted_EvenWithNoActiveQuests()
		{
			var area = new QuestArea
			{
				AreaId = 241,
				Magic = 0x57365154,
				AreaTitleRefId = 241,
				AreaTitleCategory = 50300,
				AreaTitleKey = "k-area",
				MapRefId = 4010,
				MapCategory = 52000,
				MapKey = "k-map",
				RegionRefId = 4,
				RegionCategory = 52000,
				RegionKey = "k-region"
			};

			var quest = new QuestDefinition { QuestId = 10238, AreaId = 241, JournalSequence = 1 };
			quest.TextRefs.Add(new QuestTextRef { QuestId = 10238, Slot = QuestTextSlot.Title, RefId = 10238, Category = 50301, KeyText = "k-title" });
			quest.TextRefs.Add(new QuestTextRef { QuestId = 10238, Slot = QuestTextSlot.ListSubLine, RefId = 10238, Category = 50303, KeyText = "k-sub" });

			var groups = new List<(QuestArea, IReadOnlyList<QuestDefinition>)> { (area, [quest]) };

			var withCompleted = FullCharSnapshotSubClass.BuildQuestPortion([], null, 1, groups);
			var withoutAnything = FullCharSnapshotSubClass.BuildQuestPortion([], null, 1);

			Assert.True(withCompleted.Length > withoutAnything.Length,
				"a character with no active quests but completed ones must still carry the completed journal");

			// And it must be the real region: parse it back out of the portion.
			var region = CompletedQuestJournalRegion.Parse(withCompleted, 8, withCompleted.Length, out _);
			var group = Assert.Single(region.Areas);
			Assert.Equal(241u, group.AreaId);
			Assert.Equal(10238u, Assert.Single(group.Quests).QuestId);

			// The no-quests-at-all case stays byte-identical to the capture-proven empty region.
			Assert.Equal(FullCharSnapshotSubClass.BuildEmptyQuestPortion(), withoutAnything);
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
