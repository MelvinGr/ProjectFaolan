using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Rdb;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Cli
{
	// `--report feat-name-check`: the name we SHIP for each feat against the name the client DISPLAYS (#925).
	//
	// A feat's RDB template (type 1000020, the plaintext half) carries a name inline, and that is what
	// `RdbFeatImporter.ReadNameAndDescription` returns. It is an AUTHORING name. The client draws the feat from
	// **LDB category 50220**, keyed by the same template id - established over every row in
	// `docs/rdb/ldb-string-index.md`, which measures 50220 naming 19,163 of the 19,777 feats and none of the items.
	//
	// ⚠️ The two agree on most feats, which is why this went unnoticed for so long. Where they diverge the authoring
	// name is usually the stem before a possessive - `Devourer` for `Devourer's Might`, `At Death` for
	// `At Death's Door` - so it reads as a string being truncated at an apostrophe. Nothing truncates anything;
	// they are simply two different strings, and we were shipping the wrong one.
	//
	// ⭐ Why a report and not a test: this compares SHIPPED DATA to a client artifact, and the populations move
	// whenever the RDB is re-read. A test would freeze one side of that. The direction that can only ever be a
	// defect - a feat whose displayed name we do not reproduce - is the one this returns non-zero on.
	public static partial class ReportCli
	{
		private const uint FeatNameCategory = 50220;

		private static async Task<int> FeatNameCheck(string rdbDir, string? arg = null)
		{
			using var rdb = OpenRdb(rdbDir);
			await using var db = OpenDb();

			var displayed = new Dictionary<uint, string>();
			foreach (var ((category, id), text) in LdbStringIndex.EnglishPlaintext(rdb))
				if (category == FeatNameCategory)
					displayed[id] = text;

			var feats = await db.Feats.AsNoTracking()
				.Select(f => new { f.Id, f.Name })
				.ToListAsync();

			var differing = new List<(uint Id, string Ours, string Theirs)>();
			int agree = 0, unnamed = 0, rankSuffixOnly = 0;
			foreach (var f in feats)
			{
				if (!displayed.TryGetValue(f.Id, out var theirs))
				{
					unnamed++;
					continue;
				}

				if (string.Equals(theirs, f.Name, StringComparison.Ordinal))
					agree++;
				else if (string.Equals(theirs, StripRankSuffix(f.Name), StringComparison.Ordinal))
					rankSuffixOnly++;
				else
					differing.Add((f.Id, f.Name, theirs));
			}

			Console.WriteLine($"feat names: ours vs LDB {FeatNameCategory} (the string the client draws)");
			Console.WriteLine($"  feats in the database          {feats.Count,7}");
			Console.WriteLine($"  ...named by LDB {FeatNameCategory}          {feats.Count - unnamed,7}");
			Console.WriteLine($"  ...not named there             {unnamed,7}   (the authoring name is all we have - correct to keep)");
			Console.WriteLine($"  ...identical                   {agree,7}");
			Console.WriteLine($"  ...differ ONLY by our rank tag {rankSuffixOnly,7}   (expected - see below)");
			Console.WriteLine($"  ...DIFFERENT                   {differing.Count,7}");

			// The distinct pairs, not the per-rank repetition: a feat's ranks are separate ids carrying the same name,
			// so counting rows overstates how many NAMES are wrong by roughly the rank count.
			var distinct = differing
				.GroupBy(d => (d.Ours, d.Theirs))
				.OrderByDescending(g => g.Count())
				.ToList();
			Console.WriteLine($"  ...distinct (ours -> theirs)   {distinct.Count,7}");

			// ⭐ The 983 are not one phenomenon, and only ONE of the four is safe to adopt. Classified on the pair
			// itself rather than by eye: strip the kind prefix both sides may carry (`Spell - `, `Ability - `,
			// `Combo - `) and ask which string contains which.
			var buckets = new Dictionary<string, List<(string Ours, string Theirs, int Ranks)>>();
			foreach (var g in distinct)
			{
				var ours = StripRankSuffix(g.Key.Ours);
				var (ourCore, theirCore) = (StripKind(ours), StripKind(g.Key.Theirs));
				var bucket =
					theirCore.StartsWith(ourCore, StringComparison.Ordinal) && theirCore.Length > ourCore.Length
						? "ours is a STEM of theirs      - adopt theirs"
						: ourCore.StartsWith(theirCore, StringComparison.Ordinal) && ourCore.Length > theirCore.Length
							? "theirs is SHORTER than ours   - LDB truncated, keep ours"
							: string.Equals(ourCore, theirCore, StringComparison.Ordinal)
								? "same but for the kind prefix  - adopt theirs"
								: "DISJOINT                      - unexplained, leave alone";

				if (!buckets.TryGetValue(bucket, out var list))
					buckets[bucket] = list = [];

				list.Add((g.Key.Ours, g.Key.Theirs, g.Count()));
			}

			Console.WriteLine();
			Console.WriteLine("  the distinct pairs, classified:");
			foreach (var (bucket, list) in buckets.OrderByDescending(b => b.Value.Count))
			{
				Console.WriteLine($"    {list.Count,5} pair(s) / {list.Sum(x => x.Ranks),5} row(s)  {bucket}");
				foreach (var x in list.Take(6))
					Console.WriteLine($"          {x.Ours,-40} -> {x.Theirs}");

				if (list.Count > 6)
					Console.WriteLine($"          ... and {list.Count - 6} more");
			}

			// `--report-arg tsv:<path>` writes every differing ROW with its bucket, which is what an investigation
			// into the disjoint set needs - the console print is grouped and truncated by design.
			if ((arg ?? "").StartsWith("tsv:", StringComparison.OrdinalIgnoreCase))
			{
				var path = arg![4..];
				var byPair = buckets.SelectMany(b => b.Value.Select(x => (x.Ours, x.Theirs, Bucket: b.Key)))
					.ToDictionary(x => (x.Ours, x.Theirs), x => x.Bucket);
				await using var w = new System.IO.StreamWriter(path);
				await w.WriteLineAsync("feat_id\tours\ttheirs\tbucket");
				foreach (var (id, ours, theirs) in differing.OrderBy(d => d.Id))
					await w.WriteLineAsync($"{id}\t{ours}\t{theirs}\t{byPair.GetValueOrDefault((ours, theirs), "?")}");

				Console.WriteLine($"  wrote {differing.Count} row(s) to {path}");
			}

			// ⚠️ The exit code tracks the ADOPTABLE population, not the differing one. Most of what is left is not a
			// defect: 88 pairs are an LDB entry that is truncated where ours is right, and 685 are disjoint with
			// neither side established. Returning non-zero on those would make this permanently red and therefore
			// ignorable. It goes red when a name we could safely correct is not corrected - i.e. when
			// RdbFeatImporter.PreferDisplayName has not been run over the current RDB.
			var adoptable = buckets
				.Where(b => b.Key.EndsWith("adopt theirs", StringComparison.Ordinal))
				.Sum(b => b.Value.Sum(x => x.Ranks));

			Console.WriteLine();
			Console.WriteLine(adoptable == 0
				? "  nothing safely adoptable is unadopted - the shipped names are current."
				: $"  {adoptable} row(s) could be safely corrected and are not - re-run `--only rdb-feats`.");

			return adoptable == 0 ? 0 : 1;
		}

		// ⚠️ Our `Name` keeps the RANK NUMBER the authoring name ends with (`HP 20-40 1`..`13`); the displayed name
		// does not (`HP 20-40`). That is not a defect and must not be "fixed": each rank is its own row and its own
		// template id, `ParseName` derives the `Rank` column from exactly that suffix, and adopting the displayed
		// string verbatim would give all thirteen ranks one name. So the suffix is normalised away before comparing,
		// and only what still differs is the #925 defect.
		// The client's own kind prefixes. They are part of the displayed name, but they are not what distinguishes
		// one feat from another, so a comparison that keeps them calls `Dark Hand` and `Spell - Dark Hand` disjoint.
		private static string StripKind(string name)
		{
			foreach (var kind in (string[])["Spell - ", "Ability - ", "Combo - ", "Spell- ", "Equip: "])
				if (name.StartsWith(kind, StringComparison.Ordinal))
					return name[kind.Length..];

			return name;
		}

		private static string StripRankSuffix(string name)
		{
			var i = name.LastIndexOf(' ');
			if (i <= 0 || i == name.Length - 1)
				return name;

			for (var k = i + 1; k < name.Length; k++)
				if (!char.IsAsciiDigit(name[k]))
					return name;

			return name[..i];
		}
	}
}
