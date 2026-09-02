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

		private static async Task<int> FeatNameCheck(string rdbDir)
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

			foreach (var g in distinct.Take(40))
				Console.WriteLine($"      {g.Count(),4} rank(s)  {g.Key.Ours,-42} -> {g.Key.Theirs}");

			if (distinct.Count > 40)
				Console.WriteLine($"      ... and {distinct.Count - 40} more");

			// A feat whose displayed name we do not reproduce is a defect in shipped data, in the one direction that
			// cannot be explained by the corpus growing.
			return differing.Count == 0 ? 0 : 1;
		}

		// ⚠️ Our `Name` keeps the RANK NUMBER the authoring name ends with (`HP 20-40 1`..`13`); the displayed name
		// does not (`HP 20-40`). That is not a defect and must not be "fixed": each rank is its own row and its own
		// template id, `ParseName` derives the `Rank` column from exactly that suffix, and adopting the displayed
		// string verbatim would give all thirteen ranks one name. So the suffix is normalised away before comparing,
		// and only what still differs is the #925 defect.
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
