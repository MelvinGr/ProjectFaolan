using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Codecs;
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

		// The quest INDEX, rebuilt from DB rows and compared byte-for-byte with the capture. This is the gate that
		// was missing: the index was generated from QuestDefinition columns that are 0 for every harvested quest
		// (tf1/tf2/trailer), so the server shipped a malformed index and the client died in StatList_t - while every
		// existing test stayed green, because none of them regenerated an index.
		[Fact]
		public async Task CapturedQuestIndexes_RebuildFromTheDatabase_ByteExact()
		{
			await using var db = new DatabaseContext(TestDb.Options(TestPaths.FaolanDb));
			var defs = await db.QuestDefinitions.ToDictionaryAsync(q => q.QuestId);
			var areaRows = await db.QuestAreas.ToDictionaryAsync(a => a.AreaId);
			var catalogs = (await db.QuestDefinitions.Where(q => q.AreaId != 0).ToListAsync())
				.GroupBy(q => q.AreaId)
				.ToDictionary(g => g.Key, g => (IReadOnlyList<QuestDefinition>)[.. g.OrderBy(q => q.JournalSequence)]);

			int compared = 0, exact = 0;
			var first = "";

			foreach (var (source, sub, _) in CompletedQuestJournalTests.SubClassesForProbe())
			{
				if (sub.QuestIndex is not { } captured || captured.QuestIds.Count == 0)
					continue;

				// Rebuild from the SAME quests, in the capture's order.
				var ordered = captured.QuestIds.Select(defs.GetValueOrDefault).ToList();
				if (ordered.Any(d => d == null))
					continue; // catalog gap, not a builder failure

				var built = QuestIndexRegion.Build([.. ordered!], ordered[0]!, areaRows, catalogs).ToArray();
				var want = captured.ToArray();
				compared++;
				if (built.SequenceEqual(want))
				{
					exact++;
					continue;
				}

				if (first.Length == 0)
				{
					var b = QuestIndexRegion.Parse(built, 0, built.Length);
					first = $"{source}: {built.Length} B built vs {want.Length} B captured; " +
						$"block0 tf1={b.Blocks[0].TableField1}/{captured.Blocks[0].TableField1} " +
						$"tf2={b.Blocks[0].TableField2}/{captured.Blocks[0].TableField2} " +
						$"chain=0x{b.Blocks[0].ChainWord:X}/0x{captured.Blocks[0].ChainWord:X} " +
						$"areaQuests={b.Blocks[0].AreaQuestIds.Count}/{captured.Blocks[0].AreaQuestIds.Count}";
				}
			}

			o.WriteLine($"quest indexes compared={compared} byteExact={exact}");
			if (first.Length > 0)
				o.WriteLine($"  {first}");

			Assert.True(compared > 0, "no captured quest index could be rebuilt");
			Assert.Equal(compared, exact);
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
