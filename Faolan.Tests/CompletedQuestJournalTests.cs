using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Faolan.Core;
using Faolan.Core.Network.Codecs;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Tests.Support;
using Xunit;
using Xunit.Abstractions;

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

				// The region ends where its own frame count runs out - well before the access-window table, with other
				// regions in between - so the round-trip is measured against exactly what Parse consumed.
				Assert.InRange(after, start + 4, end);
				Assert.Equal(tails[start..after], region.ToArray());

			}

			o.WriteLine($"snapshots examined={examined} withRegion={withRegion} areas={areas} completedQuests={quests}");

			// Is an "area" the same thing as a map?
			var pairs = new HashSet<(uint Area, string Region, string Map)>();
			var areaToMap = new Dictionary<uint, HashSet<string>>();
			var mapToArea = new Dictionary<string, HashSet<uint>>();
			foreach (var g in allAreas)
			{
				var reg = Id(g.RegionName);
				var map = Id(g.RegionName);
				pairs.Add((g.AreaId, reg, map));
				(areaToMap.TryGetValue(g.AreaId, out var m) ? m : areaToMap[g.AreaId] = []).Add(map);
				(mapToArea.TryGetValue(map, out var a) ? a : mapToArea[map] = []).Add(g.AreaId);
			}

			o.WriteLine($"distinct areaIds={areaToMap.Count} distinct 2nd-52000 ids={mapToArea.Count}");
			o.WriteLine($"areas whose 2nd-52000 id is NOT unique: {areaToMap.Count(kv => kv.Value.Count > 1)}");
			o.WriteLine($"2nd-52000 ids shared by >1 area: {mapToArea.Count(kv => kv.Value.Count > 1)}");
			foreach (var kv in mapToArea.OrderByDescending(k => k.Value.Count).Take(5))
				o.WriteLine($"   id {kv.Key} <- {kv.Value.Count} areas: {string.Join(",", kv.Value.Take(8))}");
			foreach (var g in allAreas.Take(4))
				o.WriteLine($"   sample area={g.AreaId} map={Id(g.MapName)} region={Id(g.RegionName)} title={Id(g.AreaTitle)}");
			Assert.True(withRegion > 0, "no snapshot in the corpus carried a completed-quest journal");
		}

		// Walks area groups from `start` until the grammar breaks or `end` is reached - how far does the region
		// actually run, and what stops it?
		private static string Exhaust(byte[] tails, int start, int end)
		{
			var r = new FaolanStream(tails, false) { Position = checked((uint)start) };
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
			var m = System.Text.RegularExpressions.Regex.Match(xml, "id=\"([0-9]+)\"");
			return m.Success ? m.Groups[1].Value : "?";
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

		private static IEnumerable<(string Source, FullCharSnapshotSubClass SubClass, byte[] Tails)> SubClasses()
		{
			foreach (var json in Directory.EnumerateFiles(TestPaths.CapturesDir, "*.tshark.json").OrderBy(f => f))
			{
				using var doc = JsonDocument.Parse(File.ReadAllBytes(json));
				if (!doc.RootElement.TryGetProperty("Packets", out var packets))
					continue;

				var index = 0;
				foreach (var p in packets.EnumerateArray())
				{
					if (!p.TryGetProperty("BodyHex", out var hexEl))
						continue;

					var hex = hexEl.GetString() ?? "";
					if (hex.Length < 40 || !hex.AsSpan(8, 8).Equals("737a6df9", StringComparison.OrdinalIgnoreCase))
						continue;

					var raw = Convert.FromHexString(hex);
					var declared = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(0, 4));
					if (declared + 4 > raw.Length)
						continue;

					FullCharSnapshotCodec.InventoryPacketParts parts;
					try
					{
						var stream = new FaolanStream(raw[4..]);
						stream.ReadUInt32();
						stream.ReadUInt32();
						stream.ReadUInt32();
						parts = FullCharSnapshotCodec.ParseInventoryPacketBody(stream);
					}
					catch
					{
						continue;
					}

					yield return ($"{Path.GetFileName(json)}#{index++}", parts.SubClass, parts.SubClass.ToArray());
				}
			}
		}
	}
}
