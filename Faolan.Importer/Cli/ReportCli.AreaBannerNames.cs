using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Rdb;
using Faolan.Importer.Rdb;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Cli
{
	// `--report area-banner-names` - every area-banner string the CLIENT knows, recovered by inverting the token hash
	// (#513/#348).
	//
	// A banner's wire value is not an LDB key. It is a parameter inside the localized token, and the LDB key is the
	// ELF-28 hash of the whole string:
	//
	//   <localized category='50025' token='DisplayedText(3)|GenericDisplayAreaName|3474586'>
	//   elf28("DisplayedText(3)|GenericDisplayAreaName|3474586") == 19303910 == cat 50025 "Castle Approach"
	//
	// That looks one-way, but the parameter is a small integer, so the hash INVERTS by sweeping it: compute the hash
	// for every candidate and keep the ones that hit a real category-50025 id. The prefix is constant, so its hash
	// state is computed ONCE and only the candidate's digits are hashed per step - which is what makes a sweep of
	// tens of millions cost a second rather than a minute.
	//
	// ⚠️ A hash hit is not proof on its own: ELF-28 has 2^28 buckets, so sweeping N candidates against a table of M
	// ids yields roughly N*M/2^28 collisions by chance. The report prints that expectation next to the hit count so
	// the reader can see whether the result is signal - and the corroboration that settles it is that the recovered
	// strings are PLACE NAMES, and that the ones we independently captured land on the right ones.
	public static partial class ReportCli
	{
		// The category the banner strings live in, and the token the client wraps a banner id in.
		private const uint AreaNameCategory = 50025;
		private const string AreaNameTokenPrefix = "DisplayedText(3)|GenericDisplayAreaName|";

		// The sweep ceiling. Every captured banner id is under 4M; this is an order of magnitude of headroom, and the
		// cost is linear and small.
		private const uint AreaNameSweepMax = 40_000_000;

		private static async Task AreaBannerNames(string rdbDir)
		{
			using var rdb = OpenRdb(rdbDir);
			var languageByPage = LdbLanguageIndex.LanguageByPage(rdb);

			// id -> English text, for the one category the banners live in.
			var byId = new Dictionary<uint, string>();
			foreach (var e in rdb.EntriesOfType(LdbLanguageIndex.PageType).Where(rdb.CanRead))
			{
				if (languageByPage.GetValueOrDefault(e.Id, "") != LdbLanguageIndex.English)
					continue;

				byte[] body;
				try
				{
					body = rdb.ReadDecompressed(e);
				}
				catch
				{
					continue;
				}

				if (LdbStringIndex.CategoryOf(body) != AreaNameCategory || LdbStringIndex.FlagsOf(body) == 1)
					continue;

				foreach (var entry in LdbStringIndex.Parse(body))
					byId[entry.Id] = entry.Value;
			}

			Console.WriteLine($"=== area-banner names, recovered by inverting elf28(token) over category {AreaNameCategory} ===");
			Console.WriteLine($"category {AreaNameCategory}: {byId.Count:N0} English entries");

			// The prefix's hash STATE, so the sweep only hashes each candidate's digits.
			var prefix = Elf28(AreaNameTokenPrefix, 0);
			var hits = new List<(uint Id, string Text)>();
			for (uint n = 0; n <= AreaNameSweepMax; n++)
			{
				if (byId.TryGetValue(Elf28Digits(n, prefix), out var text))
					hits.Add((n, text));
			}

			var expected = (double)AreaNameSweepMax * byId.Count / (1 << 28);
			Console.WriteLine($"swept 0..{AreaNameSweepMax:N0}: {hits.Count:N0} hit(s); ~{expected:F0} expected by chance alone");
			Console.WriteLine();

			// What the harvest already holds, so the recovery can be checked against it rather than admired.
			await using var db = OpenDb();
			var captured = await db.MapAreas.Select(a => a.AreaNameId).ToListAsync();
			var recovered = hits.ToDictionary(h => h.Id, h => h.Text);
			var found = captured.Where(recovered.ContainsKey).ToList();
			Console.WriteLine($"--- cross-check against the {captured.Count} CAPTURED banner ids ---");
			Console.WriteLine($"  {found.Count}/{captured.Count} recovered" + (found.Count == captured.Count ? "  <-- every one" : ""));
			foreach (var id in captured.OrderBy(i => i))
				Console.WriteLine($"    {id,-10} {(recovered.TryGetValue(id, out var t) ? t : "(not recovered)")}");

			// ---- The question this report exists to answer (#513) ----
			// A recovered id is only half a MapArea: the other half is WHICH PLAYFIELD and which volume. That join is
			// available without a capture wherever the client's own region NAME is the banner text - which is the
			// normal case outside Old Tarantia, where the regions use internal `BA_*` names instead.
			var byText = hits
				.GroupBy(h => h.Text, StringComparer.OrdinalIgnoreCase)
				.ToDictionary(g => g.Key, g => g.Select(h => h.Id).ToList(), StringComparer.OrdinalIgnoreCase);

			var ourMaps = (await db.Maps.Select(m => m.Id).ToListAsync()).ToHashSet();
			using var rdb2 = OpenRdb(rdbDir);
			int notifier = 0, named = 0, ambiguous = 0;
			var pairs = new List<string>();
			foreach (var e in rdb2.EntriesOfType(Core.Rdb.Assets.BoundedAreaCollection.ResourceType).Where(rdb2.CanRead))
			{
				if (e.Id >= BoundedAreaVariantIdBase || !ourMaps.Contains(e.Id))
					continue;

				byte[] dec;
				try
				{
					dec = rdb2.ReadDecompressed(e);
				}
				catch
				{
					continue;
				}

				if (dec.Length == 0 || !Core.Rdb.Assets.BoundedAreaCollection.TryParse(dec, out var col) || col == null)
					continue;

				foreach (var a in col.Areas.Where(a => a.IsPolygon && a.ScryId == BoundedAreaClassifier.CrossingNotifierScryId))
				{
					notifier++;
					if (!byText.TryGetValue(a.Name, out var ids))
						continue;

					if (ids.Count > 1)
					{
						ambiguous++;
						continue;
					}

					named++;
					if (pairs.Count < 30)
						pairs.Add($"    map {e.Id,-6} {a.Name,-38} -> banner {ids[0]}");
				}
			}

			Console.WriteLine();
			Console.WriteLine("--- can a MapArea be built WITHOUT a capture? (client region name == recovered banner text) ---");
			Console.WriteLine($"  crossing-notifier regions on maps we host: {notifier}");
			Console.WriteLine($"  whose name resolves to exactly one banner id: {named}   ({ambiguous} ambiguous, several ids share the text)");
			foreach (var p in pairs)
				Console.WriteLine(p);
		}

		// The classic UNIX ELF/PJW hash masked to 28 bits, resumable so a constant prefix is paid once.
		private static uint Elf28(string s, uint h)
		{
			foreach (var c in s)
			{
				h = (h << 4) + c;
				h = (h ^ ((h & 0xF0000000) >> 24)) & 0x0FFFFFFF;
			}

			return h;
		}

		// `Elf28(n.ToString(), state)` without allocating the string - the sweep runs this tens of millions of times.
		private static uint Elf28Digits(uint n, uint h)
		{
			Span<char> digits = stackalloc char[10];
			var at = digits.Length;
			do
			{
				digits[--at] = (char)('0' + (int)(n % 10));
				n /= 10;
			} while (n > 0);

			for (var i = at; i < digits.Length; i++)
			{
				h = (h << 4) + digits[i];
				h = (h ^ ((h & 0xF0000000) >> 24)) & 0x0FFFFFFF;
			}

			return h;
		}
	}
}
