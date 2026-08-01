using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Cli
{
	// `--report quest-goal-targets` - resolve every goal's TRAILING typed reference against the client's plaintext LDB
	// and say, per goal, whether that string can serve as `TargetName` (#521).
	//
	// The trailing reference is the client's own statement of what the goal is ABOUT, and its category names the table:
	// 50200 `item.name`, 51000 `npc_template.name`, 51100 `categorytree.description`, 51200 `bounded_areas.bounded_area`
	// (docs/rdb/ldb-string-index.md). All four are on the PLAINTEXT side of the LDB, so nothing here needs a wire key -
	// only a page walk, which is what #561's PlaintextLdbStringsFor does.
	//
	// ⚠️ This report exists because resolving the string is NOT the same as being able to store it. `TargetName` is a
	// MATCH key, compared against a live entity's `NpcTemplate.Name` (QuestProgress.MatchingKillGoal's name fallback,
	// ComputeGiverMarkerState). `NpcTemplate.Name` is the INTERNAL name; LDB 51000 is the DISPLAY name. Where the two
	// coincide the fill works; where they do not it silently stops matching while LOOKING resolved - strictly worse
	// than the empty column it replaced. So the useful output here is not the strings, it is the per-category
	// AGREEMENT RATE between the resolved string and the catalog the runtime actually matches against.
	public static partial class ReportCli
	{
		// The four trailing categories the goal catalog uses, and what each one's id space points at.
		private const uint ItemNameCategory = 50200;
		private const uint NpcTemplateNameCategory = 51000;
		private const uint CategoryTreeCategory = 51100;
		private const uint BoundedAreaCategory = 51200;

		private static async Task QuestGoalTargets(string rdbDir)
		{
			var plaintext = PlaintextLdbStringsFor(rdbDir);
			if (plaintext.Count == 0)
			{
				Console.WriteLine("quest-goal-targets: no ENGLISH plaintext LDB pages found - is RdbDir pointing at the client's RDB folder?");
				return;
			}

			await using var db = OpenDb();

			var goals = await db
				.QuestGoalDefinitions.AsNoTracking()
				.OrderBy(g => g.TrailingCategory)
				.ThenBy(g => g.GoalId)
				.Select(g => new
				{
					g.GoalId,
					g.QuestDefinitionId,
					g.TrailingCategory,
					g.TrailingRefId,
					g.Separator,
					g.GoalType,
					g.Partial,
					g.RequiredCount,
					g.TargetName
				})
				.ToListAsync();

			// The two catalogs a resolved string would have to agree with: NPC templates (what a kill/talk trigger
			// matches on) and items (what a collect goal counts). Names are NOT unique in either - 1,018 templates carry
			// 834 distinct names - so the by-name lookup is one-to-MANY and an ambiguous hit is reported, never picked.
			var templatesById = await db.NpcTemplates.AsNoTracking().ToDictionaryAsync(t => t.TemplateId, t => t.Name);
			var templatesByName = templatesById
				.GroupBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase)
				.ToDictionary(g => g.Key, g => g.Select(kv => kv.Key).ToArray(), StringComparer.OrdinalIgnoreCase);
			var itemsById = await db.Items.AsNoTracking().Select(i => new { i.Id, i.Name }).ToDictionaryAsync(i => i.Id, i => i.Name);

			// Which NPC names are actually STANDING somewhere. A name that resolves but is spawned nowhere makes a goal
			// look wired while still being uncompletable - the failure mode #521's Deliver half already hit.
			var spawnedNames = (await db
					.NpcSpawns.AsNoTracking()
					.Select(s => s.NpcTemplate.Name)
					.Distinct()
					.ToListAsync())
				.ToHashSet(StringComparer.OrdinalIgnoreCase);

			Console.WriteLine($"=== quest goal trailing references ({goals.Count} goals) ===");
			Console.WriteLine("goal\tquest\tcat\trefId\tkind\tgoalType\tcount\tTargetName\tLDB string\tverdict");

			var stats = new Dictionary<uint, Counters>();
			foreach (var g in goals)
			{
				if (g.TrailingRefId == 0 && g.TrailingCategory == 0)
					continue; // no trailing reference at all - nothing for this report to resolve

				var c = stats.TryGetValue(g.TrailingCategory, out var existing)
					? existing
					: stats[g.TrailingCategory] = new Counters();
				c.Goals++;
				if (!string.IsNullOrEmpty(g.TargetName))
					c.AlreadyNamed++;

				var resolved = plaintext.TryGetValue((g.TrailingCategory, g.TrailingRefId), out var text);
				if (resolved)
					c.Resolved++;

				var verdict = Verdict(g.TrailingCategory, g.TrailingRefId, resolved ? text! : null, templatesById, templatesByName, itemsById,
					spawnedNames, c);

				var kind = Enum.IsDefined(typeof(QuestGoalKind), g.Separator)
					? ((QuestGoalKind)g.Separator).ToString()
					: $"?{g.Separator}";

				Console.WriteLine(
					$"{g.GoalId}\t{g.QuestDefinitionId}\t{g.TrailingCategory}\t{g.TrailingRefId}\t{kind}\t{(g.Partial ? "(partial)" : g.GoalType.ToString())}\t{(g.Partial ? "-" : g.RequiredCount.ToString())}\t{g.TargetName ?? "-"}\t{(resolved ? Flatten(text!) : "(unresolved)")}\t{verdict}");
			}

			Console.WriteLine();
			Console.WriteLine("=== per-category agreement with the catalog the RUNTIME matches against ===");
			Console.WriteLine("cat\ttable\tgoals\tnamed\tLDB hit\tid is a catalog key\tid+name agree\tname unique\tname ambiguous\tname absent\tspawned");
			foreach (var (cat, c) in stats.OrderBy(kv => kv.Key))
			{
				Console.WriteLine(
					$"{cat}\t{TableOf(cat)}\t{c.Goals}\t{c.AlreadyNamed}\t{c.Resolved}\t{c.IdIsCatalogKey}\t{c.IdAndNameAgree}\t{c.NameUnique}\t{c.NameAmbiguous}\t{c.NameAbsent}\t{c.Spawned}");
			}

			Console.WriteLine();
			Console.WriteLine("`id is a catalog key` = the goal's refId is itself a row id in that catalog (so the string is not needed to match).");
			Console.WriteLine("`id+name agree`       = ...and the LDB string EQUALS that row's stored name - the decode check.");
			Console.WriteLine("`name unique/ambiguous/absent` = looking the LDB string up as a NAME in that catalog: one row, several, none.");

			CatalogAgreement(plaintext, NpcTemplateNameCategory, "NpcTemplates.Name (the WIRE name, from captures)", templatesById);
			CatalogAgreement(plaintext, ItemNameCategory, "Items.Name", itemsById);
		}

		// The same id+name agreement over the WHOLE catalog rather than the handful of ids the goals happen to reference.
		// This is what makes the goal-level number mean anything: six agreeing refs could be luck, and the question the
		// fill turns on - "is LDB[cat][id] the same string the runtime matches on?" - is a property of the two id spaces,
		// not of the goals. It is an INDEPENDENT check because our name column is capture-derived (the name the retail
		// server put on the wire) while the string here comes out of the client's own LDB pages.
		private static void CatalogAgreement(Dictionary<(uint Category, uint Id), string> plaintext, uint category, string label,
			Dictionary<uint, string> catalog)
		{
			int hit = 0, agree = 0;
			var disagreements = new List<string>();
			foreach (var (id, name) in catalog)
			{
				if (!plaintext.TryGetValue((category, id), out var text))
					continue;

				hit++;
				if (string.Equals(text, name, StringComparison.OrdinalIgnoreCase))
					agree++;
				else if (disagreements.Count < 15)
					disagreements.Add($"  {id}: LDB '{Flatten(text)}' vs stored '{Flatten(name)}'");
			}

			Console.WriteLine();
			Console.WriteLine($"=== LDB[{category}] vs {label}: {catalog.Count} rows, {hit} with an LDB entry, {agree} agree ===");
			foreach (var d in disagreements)
				Console.WriteLine(d);

			if (hit > disagreements.Count + agree)
				Console.WriteLine($"  ... and {hit - agree - disagreements.Count} more disagreements");
		}

		private static string TableOf(uint category) => category switch
		{
			ItemNameCategory => "item.name",
			NpcTemplateNameCategory => "npc_template.name",
			CategoryTreeCategory => "categorytree.description",
			BoundedAreaCategory => "bounded_areas.bounded_area",
			_ => "?"
		};

		// Per-goal verdict + the tally the summary reads. The question a verdict answers is always the same one: can
		// this string be written into a column the server MATCHES on, or would it only look right?
		private static string Verdict(uint category, uint refId, string? text, Dictionary<uint, string> templatesById,
			Dictionary<string, uint[]> templatesByName, Dictionary<uint, string> itemsById, HashSet<string> spawnedNames, Counters c)
		{
			if (text == null)
				return "no LDB entry";

			switch (category)
			{
				case NpcTemplateNameCategory:
				{
					var byId = templatesById.TryGetValue(refId, out var stored);
					if (byId)
					{
						c.IdIsCatalogKey++;
						var agrees = string.Equals(stored, text, StringComparison.OrdinalIgnoreCase);
						if (agrees)
							c.IdAndNameAgree++;

						CountByName(text, templatesByName, spawnedNames, c);
						return agrees
							? "refId IS the template; name agrees"
							: $"refId IS the template but its name is '{stored}'";
					}

					return "template not in catalog; " + CountByName(text, templatesByName, spawnedNames, c);
				}

				case ItemNameCategory:
				{
					if (!itemsById.TryGetValue(refId, out var stored))
						return "item not in catalog";

					c.IdIsCatalogKey++;
					if (!string.Equals(stored, text, StringComparison.OrdinalIgnoreCase))
						return $"refId IS the item but its name is '{stored}'";

					c.IdAndNameAgree++;
					return "refId IS the item; name agrees";
				}

				// A monster GROUP and a region are not rows in either catalog, so there is nothing to agree with - the
				// string is the only thing the reference yields. Still reported by name against the templates, because
				// that is the only way to see whether a group description could serve as a kill-goal name fallback.
				case CategoryTreeCategory:
					return "group description; " + CountByName(text, templatesByName, spawnedNames, c);

				case BoundedAreaCategory:
					return "region name (geometry lives in MapRegion)";

				default:
					return "unclassified category";
			}
		}

		private static string CountByName(string text, Dictionary<string, uint[]> templatesByName, HashSet<string> spawnedNames, Counters c)
		{
			if (spawnedNames.Contains(text))
				c.Spawned++;

			if (!templatesByName.TryGetValue(text, out var ids))
			{
				c.NameAbsent++;
				return "no template with that NAME";
			}

			if (ids.Length > 1)
			{
				c.NameAmbiguous++;
				return $"{ids.Length} templates share that name";
			}

			c.NameUnique++;
			return $"unique template {ids[0]}" + (spawnedNames.Contains(text) ? ", spawned" : ", NOT spawned");
		}

		private sealed class Counters
		{
			public int AlreadyNamed;
			public int Goals;
			public int IdAndNameAgree;
			public int IdIsCatalogKey;
			public int NameAbsent;
			public int NameAmbiguous;
			public int NameUnique;
			public int Resolved;
			public int Spawned;
		}
	}
}
