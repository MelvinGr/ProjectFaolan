using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Rdb.Assets;
using Faolan.Importer.Captures;
using Faolan.Importer.Extractors;
using Faolan.Importer.Rdb;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Cli
{
	// `--report area-banner-names` - every area-banner string the client knows, recovered by inverting the token hash,
	// and what the `rdb-regions` import makes of it.
	//
	// The recovery itself lives in AreaBannerNames, because it is not a report: it builds the MapArea rows. This
	// prints the same computation with its working shown - the collision expectation, the cross-check against the
	// captured ids, and the per-playfield blocks - so a change in the numbers can be read rather than inferred from a
	// row count.
	public static partial class ReportCli
	{
		// The banners the wire was seen carrying, harvested from the captures on the spot. Not read from the database:
		// a banner lives as a column on the region that fires it, so the raw sightings are a derivation input rather
		// than stored state, and a report that wants them has to harvest them the same way the importer does.
		private static List<CapturedBanner> CapturedBanners(string capturesDir)
		{
			return AreaNameExtractor.Merge(NamedCapture.LoadAll(capturesDir).SelectMany(c => AreaNameExtractor.Extract(c.Packets)));
		}

		private static async Task AreaBannerNamesReport(string rdbDir, IReadOnlyList<CapturedBanner> captured)
		{
			using var rdb = OpenRdb(rdbDir);
			var recovered = AreaBannerNames.Recover(rdb);

			Console.WriteLine($"=== area-banner names, recovered by inverting elf28(token) over category {AreaBannerNames.Category} ===");
			Console.WriteLine($"category {AreaBannerNames.Category}: {recovered.LdbEntries:N0} English entries");
			Console.WriteLine($"swept 0..{AreaBannerNames.SweepMax:N0}: {recovered.Banners.Count:N0} hit(s); ~{recovered.ExpectedCollisions:F0} expected by chance alone");
			Console.WriteLine("  a hit is NOT a banner on its own - roughly half of these are chance. What makes one real is a client");
			Console.WriteLine("  REGION of the same playfield bearing that exact name, inside that playfield's id block (see below).");
			Console.WriteLine();

			// What the harvest already holds, so the recovery can be checked against it rather than admired.
			await using var db = OpenDb();
			var byId = recovered.Banners.GroupBy(b => b.Id).ToDictionary(g => g.Key, g => g.First().Text);
			var found = captured.Count(c => byId.ContainsKey(c.AreaNameId));
			Console.WriteLine($"--- cross-check against the {captured.Count} CAPTURED banner ids ---");
			Console.WriteLine($"  {found}/{captured.Count} recovered" + (found == captured.Count ? "  <-- every one" : ""));
			foreach (var a in captured.OrderBy(a => a.AreaNameId))
				Console.WriteLine($"    map {a.MapId,-6} {a.AreaNameId,-10} {byId.GetValueOrDefault(a.AreaNameId, "(not recovered)")}");

			// ---- What the import makes of it ----
			var ourMaps = (await db.Maps.Select(m => m.Id).ToListAsync()).ToHashSet();
			var areasByMap = new Dictionary<uint, List<BoundedAreaCollection.BoundedArea>>();
			foreach (var e in rdb.EntriesOfType(BoundedAreaCollection.ResourceType).Where(rdb.CanRead))
			{
				if (e.Id >= BoundedAreaVariantIdBase || !ourMaps.Contains(e.Id))
					continue;

				byte[] dec;
				try
				{
					dec = rdb.ReadDecompressed(e);
				}
				catch
				{
					continue;
				}

				if (dec.Length == 0 || !BoundedAreaCollection.TryParse(dec, out var col) || col == null)
					continue;

				areasByMap[e.Id] = col.Areas.Where(a => a.IsPolygon).ToList();
			}

			var capturedByMap = captured
				.GroupBy(a => a.MapId)
				.ToDictionary(g => g.Key, g => (IReadOnlyCollection<uint>)g.Select(a => a.AreaNameId).ToList());

			var notes = new List<string>();
			var matches = AreaBannerNames.MatchRegions(areasByMap, recovered.Banners, capturedByMap, notes);

			var notifiers = areasByMap.Sum(kv => kv.Value.Count(a => a.ScryId == BoundedAreaClassifier.CrossingNotifierScryId));
			Console.WriteLine();
			Console.WriteLine("--- what a MapArea built WITHOUT a capture looks like (region name == recovered text, inside the playfield's id block) ---");
			Console.WriteLine($"  crossing-notifier regions on maps we host: {notifiers}");
			Console.WriteLine($"  resolved to a banner id: {matches.Count} over {matches.Select(m => m.MapId).Distinct().Count()} playfields");
			foreach (var byMap in matches.GroupBy(m => m.MapId).OrderBy(g => g.Key))
			{
				Console.WriteLine($"  -- map {byMap.Key}");
				foreach (var m in byMap.OrderBy(m => m.AreaNameId))
					Console.WriteLine($"     {m.AreaNameId,-10} {m.RegionName,-36} '{m.Text}'");
			}

			// The unresolved ones are the point of the report as much as the resolved ones: they say which playfields
			// the join does not reach and why, which is where the next evidence has to come from.
			Console.WriteLine();
			Console.WriteLine($"--- {notes.Count} region(s) the join left alone ---");
			foreach (var note in notes)
				Console.WriteLine($"  {note}");
		}
	}
}
