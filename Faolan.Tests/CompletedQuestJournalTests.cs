using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Faolan.Core;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// Corpus gate for the COMPLETED-quest journal region (Gitea #390). The region is absent from the beach template,
	// so a golden asset would prove nothing - the gate is the whole tshark corpus (see the self-referential-golden
	// -tests lesson). Every snapshot whose tails carry the region must parse by the decoded grammar AND re-emit
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
				var start = subClass.KnownHeaderLength;
				var end = AccessWindowStart(tails, start);
				if (end <= start || start + 4 > tails.Length)
					continue;

				// Only snapshots that actually carry it: the word at the boundary is the region frame, and the region
				// opens with an areaId rather than the 0x3F1 index tag.
				var lead = BinaryPrimitives.ReadUInt32BigEndian(tails.AsSpan(start, 4));
				if (lead == QuestIndexRegion.IndexTag || lead == 0 || lead % ListFrame.Unit != 0)
					continue;

				CompletedQuestJournalRegion region;
				int after;
				try
				{
					region = CompletedQuestJournalRegion.Parse(tails, start, end, out after);
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
				Assert.Equal(tails[start..after], region.ToArray());
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
			// Reads the committed CORPUS EXTRACT rather than the capture repo, so the suite runs from a plain clone.
			foreach (var (source, body) in CorpusExtract.OfType((uint)GameServer0X2000Opcodes.FullPlayerCharacterIIR))
			{
				var declared = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(0, 4));
				if (declared + 4 > body.Length)
					continue; // truncated capture - the TCP-reassembly gap on compressed downloads

				FullCharSnapshotCodec.InventoryPacketParts parts;
				try
				{
					var stream = new FaolanStream(body[4..]);
					stream.ReadUInt32();
					stream.ReadUInt32();
					stream.ReadUInt32();
					parts = FullCharSnapshotCodec.ParseInventoryPacketBody(stream);
				}
				catch
				{
					// A snapshot shape this codec cannot split - the caller's gates report coverage, so a silent
					// skip here would understate it rather than hide a wrong model.
					continue;
				}

				yield return (source, parts.SubClass, parts.SubClass.ToArray());
			}
		}
	}
}
