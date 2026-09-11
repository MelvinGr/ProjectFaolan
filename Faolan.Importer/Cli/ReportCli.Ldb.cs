using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Faolan.Core.Enums;
using Faolan.Core.Rdb;
using Faolan.Importer.Rdb;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Cli
{
	public static partial class ReportCli
	{
		// Decrypts each Ability's RDB 1000020 entry with its stored Data1..4 TEA key - the same scheme item templates
		// use - and dumps the printable strings: the ability name lives in that template.
		private static void AbilityNames(string rdbDir)
		{
			using var db = OpenDb();
			using var rdb = OpenRdb(rdbDir);
			foreach (var a in db.Abilities.OrderBy(x => x.Slot).Include(ability => ability.Key).ToList())
			{
				var key = new[] { a.Key.Key1, a.Key.Key2, a.Key.Key3, a.Key.Key4 };
				if (!rdb.TryGet(1000020, a.Id, out var e) || e.Size == 0 || !rdb.CanRead(e))
				{
					Console.WriteLine($"0x{a.Id:X8} slot={a.Slot,-5} (no 1000020 entry)");
					continue;
				}

				byte[] body;
				try
				{
					body = rdb.ReadDecrypted(e, key);
				}
				catch (Exception ex)
				{
					Console.WriteLine($"0x{a.Id:X8} (decrypt failed: {ex.Message})");
					continue;
				}

				var name = RdbItemImporter.ExtractName(body);
				var fields = ReadIncreasingFields(body.AsSpan(12));
				var fieldStr = string.Join(" ", fields.OrderBy(f => f.Key).Select(f => $"{f.Key}={f.Value}"));

				// Abilities are the same 1000020 template layout as items, spells and feats, so the shared parser
				// applies - but the answer is that they carry no stat-modifier region. Of the 27 decryptable
				// abilities only 10 yield anything and all 10 are the parser's documented false-positive mode: the
				// "stats" are the directional ids the field table already shows (Upper Left Attack -> 201, Overhead
				// -> 203), or zero-valued (the four pet commands -> stats 1..4 = 0), or a kind-range number as an
				// amount (a mount ability -> 1890 = 53202). Their kinds (53043, 53251) are none of the flat/percent
				// tags either. Shown here so the emptiness is visible rather than assumed - do not import these.
				var mods = ItemTemplateStatModifiers.Parse(body.AsSpan(12));
				var modStr = mods.Count == 0
					? ""
					: "  | MODS " + string.Join(" ", mods.Select(m => $"{m.StatId}={m.WireAmount}(k{m.Kind})"));
				Console.WriteLine($"0x{a.Id:X8} slot={a.Slot,-5} \"{name}\"  | {fieldStr}{modStr}");
			}
		}

		// The client's own stat-name table: LDB category 615 is an id -> name map keyed by the wire stat id - the same
		// id space as the 0x96B8DC59 vitals block, the FullChar snapshot stat sheet, the item [2][9] modifier region
		// and the spell stat effects.
		//
		// Like every LDB category it is localized - one page per language, all carrying the same ids - so the page
		// must be cribbed rather than picked by order, since taking the last page yields Korean. The crib is the one
		// the spell and item name reports use: the English page is the one whose text matches CharacterStatId's own
		// names. That cross-check is also what makes this a linter rather than a dump - a locally invented name that
		// disagrees with the client's is a bug, and an enum id absent from the table is one named from somewhere else
		// (Ghidra, tooltips) and should stay justified in its comment.
		private static void LdbStatNames(string rdbDir, string? outDir)
		{
			using var rdb = OpenRdb(rdbDir);

			// Our own names, normalized to letters+digits, as the crib key set.
			static string Key(string s)
			{
				return Regex.Replace(s, "[^A-Za-z0-9]", "").ToLowerInvariant();
			}

			var ourNames = Enum.GetValues<CharacterStatId>().Distinct().ToDictionary(id => (uint)id, id => Key(Enum.GetName(id)!));

			var scored = LdbPagesOf(rdb)
				.Where(p => p.Category == LdbStringIndex.StatNameCategory)
				.Select(p => (p.PageId, p.Strings, Match: p.Strings.Count(kv => ourNames.TryGetValue(kv.Key, out var ours) && Key(kv.Value) == ours)))
				.OrderByDescending(x => x.Match)
				.ToList();

			if (scored.Count == 0)
			{
				Console.WriteLine($"LDB cat {LdbStringIndex.StatNameCategory}: no pages found - is this RDB dir complete?");
				return;
			}

			Console.WriteLine($"LDB cat {LdbStringIndex.StatNameCategory}: {scored.Count} page(s); cribbed against {ourNames.Count} named CharacterStatId members:");
			foreach (var (pageId, strings, match) in scored)
				Console.WriteLine($"  page {pageId,-9} ids={strings.Count,-5} crib-match={match,-4} {(pageId == scored[0].PageId ? "<-- ENGLISH" : "")}");

			var names = scored[0].Strings;

			var lines = names.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}\t0x{kv.Key:X4}\t{kv.Value}").ToList();
			foreach (var line in lines)
				Console.WriteLine("  " + line);

			// Cross-check: every CharacterStatId member against the client's name for that id.
			int agree = 0, absent = 0;
			var disagree = new List<string>();
			foreach (var id in Enum.GetValues<CharacterStatId>().Distinct())
			{
				var member = Enum.GetName(id)!;
				if (!names.TryGetValue((uint)id, out var ldb))
				{
					absent++;
					continue;
				}

				// Compare on letters+digits only: our names are PascalCase, the client's are spaced/parenthesised.
				var ours = Regex.Replace(member, "[^A-Za-z0-9]", "").ToLowerInvariant();
				var theirs = Regex.Replace(ldb, "[^A-Za-z0-9]", "").ToLowerInvariant();
				if (ours == theirs)
					agree++;
				else
					disagree.Add($"  0x{(uint)id:X4} {member} != '{ldb}'");
			}

			Console.WriteLine($"CharacterStatId: {agree} names match the client's, {disagree.Count} differ, {absent} have no entry in the table.");
			foreach (var d in disagree)
				Console.WriteLine(d);

			StatNameReconcile(names);

			if (string.IsNullOrEmpty(outDir))
				return;

			Directory.CreateDirectory(outDir);
			var path = Path.Combine(outDir, "ldb-stat-names.tsv");
			File.WriteAllLines(path, lines);
			Console.WriteLine($"wrote {path}");
		}
	}
}
