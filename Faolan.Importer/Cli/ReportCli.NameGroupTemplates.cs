using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Faolan.Core.Rdb;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Cli
{
	// `--report name-group-templates`: can the CLIENT name the creature behind an unresolved `Name`-category spawn
	// group? (Gitea #684.)
	//
	// #684 measured that 153 `Name`-category `NpcSpawnArea`s carry a real creature name over 31 distinct creatures,
	// and that "we hold an `NpcTemplate` for exactly 0 of them", concluding **"spawn coverage is capture-bound, not
	// decode-bound"** and reducing the ticket to a shopping list of captures to wish for.
	//
	// ⭐ That is only half true, and this report is the half it missed. `LDB[51000]` is the client's NPC-template
	// name table and is **keyed by the template id** - verified over the whole catalog: all 1,018 `NpcTemplates`
	// have an entry and 991 agree exactly with the capture-derived `Name`, an independent cross-check because our
	// name came off the wire and the LDB's did not. Read BACKWARDS, `name -> templateId`, it answers exactly the
	// question the resolver's tier 1 asks and cannot currently answer: the client knows what `Shar Kihn (4YQ1)` is,
	// and it knows its retail template id.
	//
	// ⚠️ What that does NOT do is make the creature spawnable. A template id plus a display name is an IDENTITY, not
	// an entity: level, health, model, faction and profession are all still capture-bound, and #684's own
	// `rdb_npc_hp_not_in_client` finding stands. So the report deliberately reports the gap in two columns rather
	// than one - what the client can name, and what is still missing - and writes nothing. Manufacturing a stats-less
	// `NpcTemplate` to satisfy an FK would ship a creature that spawns wrong, which is worse than one that does not
	// spawn.
	//
	// ⚠️ Names are matched EXACTLY (case-insensitively) after stripping the 4-char tag, the same normalisation tier 1
	// applies. A fuzzy match here would be the worst kind of false positive: it would silently bind a spawn group to
	// the wrong creature, and nothing downstream would ever question it.
	public static partial class ReportCli
	{
		// The 4-char code the client appends to a Name-category group key: `Shar Kihn (4YQ1)`.
		private static readonly Regex NameGroupTag = new Regex(@"\s*\([0-9A-Za-z]{4}\)\s*$", RegexOptions.Compiled);

		// Dev/test content that must NOT be chased - #684 names these explicitly, and a resolution for one of them
		// would be a false lead rather than a win.
		private static readonly Regex DevContent = new Regex(@"^(XX[ _]|NonAggro Dummy)", RegexOptions.Compiled);

		// The group key with its 4-char tag stripped - the same normalisation the resolver's tier 1 applies, so a hit
		// here means tier 1 would take it the moment an NpcTemplate row exists.
		private static string Normalise(string? groupKey)
		{
			var name = NameGroupTag.Replace(groupKey ?? "", "").Trim();
			return string.Equals(name, "no category value", StringComparison.OrdinalIgnoreCase) || name == "0" ? "" : name;
		}

		private static async Task NameGroupTemplates(string rdbDir)
		{
			await using var db = OpenDb();
			var ct = System.Threading.CancellationToken.None;

			var groups = await db.NpcSpawnAreas
				.Where(a => a.Category == "Name" && a.NpcTemplateId == null)
				.Select(a => new { a.GroupKey, a.MapId })
				.ToListAsync(ct);

			var held = await db.NpcTemplates.Where(t => t.Name != null)
				.Select(t => new { t.TemplateId, t.Name })
				.ToListAsync(ct);

			var heldByName = held.GroupBy(t => t.Name!.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First().TemplateId);
			var heldIds = held.Select(t => t.TemplateId).ToHashSet();

			// name -> template id, out of the client's own table. Built once, walked backwards.
			using var rdb = OpenRdb(rdbDir);
			var templateByName = new Dictionary<string, List<uint>>(StringComparer.OrdinalIgnoreCase);
			var ldbRows = 0;
			foreach (var (_, category, strings) in EnglishLdbPagesOf(rdb))
			{
				if (category != NpcTemplateNameCategory)
					continue;

				foreach (var (id, value) in strings)
				{
					if (string.IsNullOrWhiteSpace(value))
						continue;

					ldbRows++;
					if (!templateByName.TryGetValue(value, out var ids))
						templateByName[value] = ids = [];

					ids.Add(id);
				}
			}

			Console.WriteLine($"LDB[{NpcTemplateNameCategory}] holds {ldbRows:N0} template name(s) over {templateByName.Count:N0} distinct string(s).");
			Console.WriteLine($"{groups.Count} unresolved `Name`-category spawn area(s) over "
			                  + $"{groups.Select(g => Normalise(g.GroupKey)).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count()} distinct creature name(s).");
			Console.WriteLine();

			var rows = new List<(string Name, int Areas, string Maps, string Verdict)>();
			foreach (var byName in groups.GroupBy(g => Normalise(g.GroupKey), StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
			{
				var name = byName.Key;
				var maps = string.Join(",", byName.Select(g => g.MapId).Distinct().Order());

				string verdict;
				if (name.Length == 0)
					verdict = "NO NAME - the group declares `Name` and carries none (unresolvable from the client)";
				else if (DevContent.IsMatch(name))
					verdict = "DEV CONTENT - do not chase (#684)";
				else if (heldByName.ContainsKey(name.ToLowerInvariant()))
					verdict = $"ALREADY HELD as template {heldByName[name.ToLowerInvariant()]} - the resolver should have taken it";
				else if (!templateByName.TryGetValue(name, out var ids))
					verdict = "NOT IN LDB 51000 either - the client does not name it";
				else if (ids.Count == 1)
					verdict = $"⭐ UNAMBIGUOUS: template {ids[0]}" + (heldIds.Contains(ids[0]) ? " (and we HOLD that id under another name!)" : "");
				else
					verdict = $"⚠️ AMBIGUOUS: {ids.Count} templates share the name - {string.Join("/", ids.Order())}";

				rows.Add((name.Length == 0 ? "(no name)" : name, byName.Count(), maps, verdict));
			}

			Console.WriteLine($"  {"creature",-30} {"areas",5}  {"maps",-22} verdict");
			foreach (var (name, areas, maps, verdict) in rows.OrderByDescending(r => r.Verdict.StartsWith('⭐')).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
				Console.WriteLine($"  {Trim(name, 30),-30} {areas,5}  {Trim(maps, 22),-22} {verdict}");

			var named = rows.Where(r => r.Verdict.StartsWith('⭐')).ToList();
			var ambiguous = rows.Where(r => r.Verdict.StartsWith('⚠')).ToList();
			Console.WriteLine();
			Console.WriteLine($"⭐ {named.Count} of {rows.Count} creature(s) - {named.Sum(r => r.Areas)} of {groups.Count} spawn area(s) - are named by the");
			Console.WriteLine("   client with ONE retail template id, so that group->creature link is recoverable STATICALLY.");
			Console.WriteLine($"⚠️ {ambiguous.Count} more ({ambiguous.Sum(r => r.Areas)} area(s)) are named but NOT UNIQUELY: several templates share the");
			Console.WriteLine("   string, which is the same-name-different-number footgun. The 4-char tag beside the name does");
			Console.WriteLine("   NOT break the tie either - `Shark (H8AV)` and `Coast Shark (H8AV)` share one - and the tag is an");
			Console.WriteLine("   ACGHash-family authoring token with no algorithm to reproduce (#723). Left unresolved.");
			Console.WriteLine();
			Console.WriteLine("⚠️ And a name is an IDENTITY, not an entity. Level, health, model, faction and profession are still");
			Console.WriteLine("   capture-bound (NPC HP is not in the client at all), so nothing is written: a stats-less");
			Console.WriteLine("   NpcTemplate would spawn a creature that is wrong rather than one that is absent.");
		}
	}
}
