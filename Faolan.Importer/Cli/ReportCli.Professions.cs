using System;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Cli
{
	// `--report professions` - resolve every `NpcStatCore.ProfessionName` against the client's own plaintext LDB, and
	// print the profession catalog it produces: type -> name -> the templates that carry it (Gitea #649).
	//
	// `ProfessionName` (`ProtoCoreCharacterIIR.NpcStats.profession_name`, field 11) is the ELF hash of the profession's
	// NAME string - identified in #647 by two exact wordlist hits, `elf("Merchant") == 210363236` and
	// `elf("Trainer") == 193463442`. An ELF-hashed id is how the LDB addresses its strings, so the hash is an LDB ID
	// and the name is a lookup rather than a brute force. This report performs that lookup.
	//
	// It exists as a REPORT rather than a scratch script because the answer is a survey, not a one-off: the useful
	// output is the per-TYPE grouping (does the resolved name agree with what the templates carrying it look like?)
	// and the RESIDUE (which hashes still resolve to nothing, and why). Both change as the LDB coverage and the
	// imported world change, so re-running it has to be one command.
	//
	// **Result: all 88 resolve, and LDB category 253 is the table.** It holds the profession/shop-kind label - "Trader",
	// "Merchant", "Local Travel", "Guard", "Armorsmith", "Master Potions", "Cimmerian Pathfinder", ... - which is what
	// names `NpcProfession`'s members and what identifies a vendor's shop category.
	//
	// ⚠️ Eight of the ids are TINY (1, 2, 22, 28, 44, 46, 53, 56), far below any 28-bit ELF hash of a real word, and
	// they resolve in category 253 as well. So the column is NOT uniformly hashed: category 253 carries small
	// sequential ids beside the hashed ones, and an id resolves the same way either way. They are called out because
	// assuming one encoding is exactly the mistake this report should prevent - a small id also resolves in every OTHER
	// category, so it is only unambiguous once the category is pinned.
	public static partial class ReportCli
	{
		// The client's profession / shop-kind string table. Every ProfessionName in the shipped data resolves here.
		private const uint ProfessionNameCategory = 253;

		// Below this an id cannot be the ELF hash of a real name: ELF shifts left 4 bits per character, so even a
		// 3-character string lands above 2^12. The observed small ids are three orders of magnitude under it.
		private const uint SmallestPlausibleHash = 1 << 12;

		private static async Task Professions(string rdbDir)
		{
			var plaintext = PlaintextLdbStringsFor(rdbDir);
			if (plaintext.Count == 0)
			{
				Console.WriteLine("professions: no ENGLISH plaintext LDB pages found - is RdbDir pointing at the client's RDB folder?");
				return;
			}

			await using var db = OpenDb();
			var rows = await db.NpcStatCores.AsNoTracking()
				.Join(db.NpcTemplates.AsNoTracking(), c => c.NpcTemplateId, t => t.TemplateId, (c, t) => new
				{
					c.ProfessionType,
					c.ProfessionName,
					t.TemplateId,
					t.Name
				})
				.Where(x => x.ProfessionName != null)
				.ToListAsync();

			if (rows.Count == 0)
			{
				Console.WriteLine("professions: no NpcStatCore rows carry a ProfessionName - has the npcs module run?");
				return;
			}

			var names = plaintext
				.Where(kv => kv.Key.Category == ProfessionNameCategory)
				.ToDictionary(kv => kv.Key.Id, kv => kv.Value);

			var ids = rows.Select(r => r.ProfessionName!.Value).Distinct().OrderBy(h => h).ToList();
			var resolved = ids.Where(names.ContainsKey).ToList();
			var small = ids.Where(h => h < SmallestPlausibleHash).ToList();

			Console.WriteLine($"professions: {rows.Count} templates carry a ProfessionName; {ids.Count} distinct value(s).");
			Console.WriteLine($"  resolved in LDB[{ProfessionNameCategory}] : {resolved.Count}");
			Console.WriteLine($"  unresolved                : {string.Join(", ", ids.Except(resolved))}");
			Console.WriteLine($"  small ids (not hashes)    : {small.Count}  ({string.Join(", ", small)})");
			Console.WriteLine($"  LDB[{ProfessionNameCategory}] holds {names.Count} string(s) in total.");

			Console.WriteLine();
			foreach (var group in rows.GroupBy(r => r.ProfessionType).OrderBy(g => g.Key ?? uint.MaxValue))
			{
				var typeName = group.Key is { } t && Enum.IsDefined((NpcProfession)t)
					? ((NpcProfession)t).ToString()
					: group.Key?.ToString() ?? "(unset)";
				Console.WriteLine($"== ProfessionType {group.Key?.ToString() ?? "-"} ({typeName}): {group.Count()} template(s)");

				foreach (var name in group.GroupBy(r => r.ProfessionName!.Value).OrderByDescending(g => g.Count()))
				{
					var text = names.TryGetValue(name.Key, out var hit)
						? $"\"{hit}\""
						: "<unresolved>";

					var examples = name.Select(x => x.Name).Where(n => !string.IsNullOrEmpty(n)).Distinct().Take(4);
					Console.WriteLine($"   {name.Key,11}  x{name.Count(),-4} {text}");
					Console.WriteLine($"               e.g. {string.Join(", ", examples)}");
				}

				Console.WriteLine();
			}
		}
	}
}
