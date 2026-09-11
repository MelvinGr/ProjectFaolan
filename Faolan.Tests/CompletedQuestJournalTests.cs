using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Faolan.Core;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// Corpus gate for the completed-quest journal region. The region is absent from the beach template,
	// so a golden asset would prove nothing - the gate is the whole tshark corpus (see the self-referential-golden
	// -tests lesson). Every snapshot whose tails carry the region must parse by the decoded grammar and re-emit
	// byte-for-byte.
	public class CompletedQuestJournalTests(ITestOutputHelper o)
	{
		[Fact]
		public void EveryCapturedRegion_RoundTripsByteExact()
		{
			int examined = 0, withRegion = 0, quests = 0, areas = 0;
			var allAreas = new List<CompletedQuestJournalRegion.AreaGroup>();
			var perSnapshot = new List<(string Source, CompletedQuestJournalRegion Region)>();

			foreach (var (source, subClass, tails) in SubClasses())
			{
				examined++;

				// The region sits between the active journal and the access-window table.
				var start = SnapshotPackets.JournalLength(subClass);
				var end = AccessWindowStart(tails, start);
				if (end <= start || start + 4 > tails.Length)
					continue;

				// Only snapshots that actually carry it: the word at the boundary is the region frame, and the region
				// opens with an areaId rather than the 0x3F1 index tag.
				var lead = tails.ReadU32Be(start);
				if (lead == ListFrame.Empty || lead == 0 || lead % ListFrame.Unit != 0)
					continue;

				CompletedQuestJournalRegion region;
				int after;
				try
				{
					var r = new SpanReader(tails.AsSpan(start, end - start), nameof(CompletedQuestJournalRegionCodec));
					region = CompletedQuestJournalRegionCodec.Read(ref r);
					after = start + r.Position;
				}
				catch (Exception ex)
				{
					Assert.Fail($"{source}: completed journal failed to parse at {start}..{end}: {ex.Message}");
					return;
				}

				withRegion++;
				quests += region.QuestCount;
				areas += region.Areas.Count;
				allAreas.AddRange(region.Areas);
				perSnapshot.Add((source, region));

				// The region ends where its own frame count runs out - well before the access-window table, with other
				// regions in between - so the round-trip is measured against exactly what Parse consumed.
				Assert.InRange(after, start + 4, end);
				Assert.Equal(tails[start..after], CompletedQuestJournalRegionCodec.Build(region));
			}

			o.WriteLine($"snapshots examined={examined} withRegion={withRegion} areas={areas} completedQuests={quests}");

			// Why would an @609 goal have MapId 0?
			int goals = 0, zeroMap = 0;
			var zeroSamples = new List<string>();
			var mapVals = new Dictionary<uint, int>();
			foreach (var (src, sub, _) in SubClasses())
			foreach (var rec in sub.Quests.Records)
			{
				if (rec.Objectives is not { Goals.Count: > 0 } obj)
					continue;

				foreach (var g in obj.Goals)
				{
					goals++;
					mapVals[g.MapId] = mapVals.GetValueOrDefault(g.MapId) + 1;
					if (g.MapId != 0)
						continue;

					zeroMap++;
					if (g.MarkerX != 0 || g.MarkerY != 0)
						zeroSamples.Add($"COUNTER-EXAMPLE quest {rec.QuestId} goal {g.GoalId}: mapId=0 but marker=({g.MarkerX},{g.MarkerY})");
				}
			}

			o.WriteLine($"@609 goals={goals} withMapId0={zeroMap} distinctMapIds={mapVals.Count}");
			o.WriteLine($"  mapId==0 goals that still carry a marker: {zeroSamples.Count}");
			o.WriteLine("  top mapIds: " + string.Join(" ", mapVals.OrderByDescending(k => k.Value).Take(8).Select(k => $"{k.Key}:{k.Value}")));
			foreach (var x in zeroSamples)
				o.WriteLine($"    {x}");

			Assert.True(withRegion > 0, "no snapshot in the corpus carried a completed-quest journal");
		}

		// The parsed completed-quest journal of every capture that carries one - shared with the generation gate.
		public static IEnumerable<(string Source, CompletedQuestJournalRegion Region)> CapturedRegions()
		{
			foreach (var (source, subClass, tails) in SubClasses())
			{
				if (subClass.CompletedQuests is { Areas.Count: > 0 } region)
					yield return (source, region);
			}
		}

		// The word after a completed line group's 4CC is the line's flag - the same per-line flag the quest index
		// block carries as TrailerC (MainQuest_c +0x50), which is why one QuestLine.TrailerFlag column now feeds both
		// producers. The gate is the agreement on every line a snapshot carries in both records, and it only
		// binds if at least one such line is flagged - all-zero agreement would not distinguish one field from two.
		[Fact]
		public void TheLineFlag_AgreesWithTheIndexBlocksTrailerC_OnEveryLineSeenInBoth()
		{
			var seenInBoth = new HashSet<uint>();
			var flagged = new HashSet<uint>();
			foreach (var (source, sub, _) in SubClasses())
			{
				if (sub.CompletedQuests is not { } completed || sub.QuestIndex is not { } index)
					continue;

				var flagByLine = index.Blocks.ToDictionary(b => b.AreaId, b => b.TrailerC);
				foreach (var group in completed.Areas)
				{
					if (!flagByLine.TryGetValue(group.QuestLineId, out var trailerC))
						continue;

					Assert.True(group.LineFlag == trailerC, $"{source}: line {group.QuestLineId} completed-journal flag {group.LineFlag} vs index TrailerC {trailerC}");
					seenInBoth.Add(group.QuestLineId);
					if (trailerC != 0)
						flagged.Add(group.QuestLineId);
				}
			}

			o.WriteLine($"lines seen in both records={seenInBoth.Count} flagged={flagged.Count} [{string.Join(",", flagged)}]");
			Assert.True(seenInBoth.Count > 0, "no snapshot carries a line in both the completed journal and the index");
			Assert.True(flagged.Count > 0, "no flagged line is captured in both records, so the gate cannot tell one field from two");
		}

		// The third producer of the same flag: the QuestJournalIIR's area block is the completed journal's line
		// record through the same client reader (`FUN_00922c90`), so its word after the ACGHash must carry what the
		// snapshot's index block carries as TrailerC for that line. Every captured journal area is on a flag-0 line, so
		// this gate cannot tell "one field" from "always 0" by variance - the identification is the reader, one
		// function with two callers - but it binds the three producers to one value on every line the corpus carries
		// in both, and a journal that started carrying a value the index does not would fail here.
		[Fact]
		public void TheJournalAreasTrailerFlag_AgreesWithTheIndexBlocksTrailerC_OnEveryLineSeenInBoth()
		{
			var trailerCByLine = new Dictionary<uint, uint>();
			foreach (var (source, sub, _) in SubClasses())
			{
				if (sub.QuestIndex is not { } index)
					continue;

				foreach (var block in index.Blocks)
				{
					if (trailerCByLine.TryGetValue(block.AreaId, out var seen))
						Assert.True(seen == block.TrailerC, $"{source}: line {block.AreaId} TrailerC {block.TrailerC} vs an earlier block's {seen}");
					else
						trailerCByLine[block.AreaId] = block.TrailerC;
				}
			}

			var areas = 0;
			var inBoth = new HashSet<uint>();
			foreach (var (source, body) in CorpusExtract.OfType((uint)IirTypes.QuestJournalIIR))
			{
				var declared = (int)body.ReadU32Be(0);
				if (declared + 4 > body.Length)
					continue; // truncated capture

				var journal = QuestJournalCodec.Parse(body[4..(4 + declared)]);
				foreach (var area in journal.MoreAreas.Prepend(journal.Area))
				{
					areas++;
					if (!trailerCByLine.TryGetValue(area.QuestLineId, out var trailerC))
						continue;

					Assert.True(area.TrailerFlag == trailerC, $"{source}: journal area {area.QuestLineId} flag {area.TrailerFlag} vs index TrailerC {trailerC}");
					inBoth.Add(area.QuestLineId);
				}
			}

			o.WriteLine($"journal areas={areas} lines seen in a journal AND an index block={inBoth.Count}");
			Assert.True(areas > 0, "no captured journal area");
			Assert.True(inBoth.Count > 0, "no journal area's line is on a captured index block, so the gate binds nothing");
		}

		public static IEnumerable<(string Source, FullCharSnapshotSubClass SubClass, byte[] Tails)> SubClassesForProbe()
		{
			return SubClasses();
		}

		// The access-window table starts 15 bytes before the first PFAccessWindow_ name (mirrors
		// FullCharSnapshotSubClass.Deserialize).
		private static int AccessWindowStart(byte[] tails, int notBefore)
		{
			var needle = "PFAccessWindow_"u8.ToArray();
			for (var i = notBefore; i + needle.Length <= tails.Length; i++)
			{
				var hit = true;
				for (var k = 0; k < needle.Length && hit; k++)
					hit = tails[i + k] == needle[k];

				if (hit)
					return Math.Max(notBefore, i - 15);
			}

			return -1;
		}

		// Walks area groups from `start` until the grammar breaks or `end` is reached - how far does the region
		// actually run, and what stops it?
		private static string Exhaust(byte[] tails, int start, int end)
		{
			var r = new FaolanStream(tails, false)
			{
				Position = checked((uint)start)
			};
			r.ReadUInt32(); // frame
			int areas = 0, quests = 0;
			try
			{
				while (r.Position < end)
				{
					r.ReadUInt32();
					r.ReadString();
					r.ReadString();
					r.ReadString();
					r.ReadUInt32();
					r.ReadUInt32();
					var n = r.ReadUInt32();
					if (n > 4096)
						return $"areas={areas} quests={quests} STOP at {r.Position}: questsInArea={n} implausible";

					for (var q = 0; q < n; q++)
					{
						r.ReadUInt32();
						r.ReadUInt32();
						var f = r.ReadUInt32();
						if (f == 0 || f % ListFrame.Unit != 0)
							return $"areas={areas} quests={quests} STOP at {r.Position}: record frame 0x{f:X} not a list word";

						for (var k = 0; k < ListFrame.CountOf(f); k++)
							r.ReadString();

						quests++;
					}

					areas++;
				}

				return $"areas={areas} quests={quests} reached end {end} CLEANLY";
			}
			catch (Exception ex)
			{
				return $"areas={areas} quests={quests} THREW at {r.Position}: {ex.Message}";
			}
		}

		private static string Id(string xml)
		{
			var m = Regex.Match(xml, "id=\"([0-9]+)\"");
			return m.Success
				? m.Groups[1].Value
				: "?";
		}

		private static IEnumerable<(string Source, FullCharSnapshotSubClass SubClass, byte[] Tails)> SubClasses()
		{
			// Reads the committed corpus extract rather than the capture repo, so the suite runs from a plain clone.
			foreach (var (source, body) in CorpusExtract.OfType((uint)IirTypes.FullPlayerCharacterIIR))
			{
				var declared = body.ReadU32Be(0);
				if (declared + 4 > body.Length)
					continue; // truncated capture - the TCP-reassembly gap on compressed downloads

				FullPlayerCharacterIIR parts;
				try
				{
					var stream = new FaolanStream(body[4..]);
					stream.ReadUInt32();
					stream.ReadUInt32();
					stream.ReadUInt32();
					parts = SnapshotPackets.Parse(stream);
				}
				catch
				{
					// A snapshot shape this codec cannot split - the caller's gates report coverage, so a silent
					// skip here would understate it rather than hide a wrong model.
					continue;
				}

				yield return (source, parts.SubClass, FullCharSnapshotSubClassCodec.Build(parts.SubClass));
			}
		}
	}
}
