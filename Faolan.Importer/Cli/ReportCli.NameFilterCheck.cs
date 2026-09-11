using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Progression;
using Faolan.Core.Rdb;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Cli
{
	// `--report name-filter-check`: is the character-name rule too blunt for the world the client ships?
	//
	// `CharacterNameRules` matches a forbidden term as a SUBSTRING once it is long enough, and retail's own matching
	// rule is unobservable — no capture rejects a name. So the floor is calibrated against an oracle the client
	// supplies instead: the words in its own NPC and item names are by construction acceptable English, and a rule
	// that refuses them is refusing names players will reasonably want. This prints the refusal count per candidate
	// floor, so the constant can be re-derived rather than trusted.
	//
	// Why a report and not a test: it compares shipped data — `NameFilterTerms` and the NPC/item
	// vocabulary in `faolan.db` — against importer input, the client's own RDB. Those two populations move whenever
	// the client or the import does, so as a test it would fail on data that is more correct than its baseline.
	//
	// It also prints rather than asserts, with one exception: a shipped row whose term the client does not carry
	// means `faolan.db` holds a filter term this client cannot justify, and that can only be a defect.
	public static partial class ReportCli
	{
		private static async Task<int> NameFilterCheck(string rdbDir)
		{
			using var rdb = new RdbReader(rdbDir, null);
			var shipped = RdbNameFilterList.Load(rdb);
			var usable = shipped.Select(t => t.Trim()).Where(CharacterNameRules.IsUsableTerm).ToList();
			var byHash = usable.GroupBy(CharacterNameRules.Hash).ToDictionary(g => g.Key, g => g.First());

			await using var db = OpenDb();
			var rows = await db.NameFilterTerms.AsNoTracking().ToListAsync();

			Console.WriteLine($"name-filter-check: RDB {RdbNameFilterList.Type} ships {shipped.Count} terms, "
			                  + $"{usable.Count} usable / {byHash.Count} distinct; faolan.db holds {rows.Count} rows.");

			var orphans = rows.Where(r => !byHash.ContainsKey(r.TermHash)).ToList();
			var missing = byHash.Keys.Where(h => rows.All(r => r.TermHash != h)).ToList();
			Console.WriteLine($"  rows this client does not justify: {orphans.Count}   client terms not imported: {missing.Count}"
			                  + (missing.Count == 0 ? "" : "  (run `--only rdb-name-filter`)"));

			// The client's own vocabulary, one token at a time - a character name is a single token, so multi-word
			// NPC names contribute their words rather than themselves.
			var vocabulary = (await db.NpcTemplates.AsNoTracking().Where(n => n.Name != null).Select(n => n.Name!).ToListAsync())
				.Concat(await db.Items.AsNoTracking().Where(i => i.Name != null).Select(i => i.Name!).ToListAsync())
				.SelectMany(n => n.Replace('\'', ' ').Replace('-', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries))
				.Where(w => w.Length is >= CharacterNameRules.MinLength and <= CharacterNameRules.MaxLength && w.All(char.IsLetter))
				.Select(w => w.ToLowerInvariant())
				.Distinct()
				.ToList();

			Console.WriteLine($"  client vocabulary: {vocabulary.Count} name-shaped tokens");
			Console.WriteLine("  substring floor   terms matched anywhere   client words refused");
			foreach (var floor in new[] { 3, 4, 5, 6, 7, 8 })
			{
				var refused = vocabulary.Where(w => Refuses(w, byHash, floor)).ToList();
				var marker = floor == CharacterNameRules.SubstringMinLength ? " <- shipped" : "";
				Console.WriteLine($"  {floor,13}   {byHash.Values.Count(t => t.Length >= floor),21}   "
				                  + $"{refused.Count,20}{marker}  {string.Join(", ", refused.Take(6))}");
			}

			if (orphans.Count == 0)
				return 0;

			Console.WriteLine($"  ⛔ {orphans.Count} shipped row(s) match no term this client carries.");
			return 1;
		}

		// The rule under test, with the floor as a parameter - CharacterNameRules.Check hard-codes the shipped one.
		private static bool Refuses(string lowered, Dictionary<string, string> byHash, int floor)
		{
			if (byHash.ContainsKey(CharacterNameRules.Hash(lowered)))
				return true;

			for (var length = floor; length < lowered.Length; length++)
			{
				for (var start = 0; start + length <= lowered.Length; start++)
				{
					if (byHash.ContainsKey(CharacterNameRules.Hash(lowered.Substring(start, length))))
						return true;
				}
			}

			return false;
		}
	}
}
