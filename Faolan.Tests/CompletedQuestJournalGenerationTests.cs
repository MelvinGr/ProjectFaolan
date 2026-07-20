using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
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
