using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Rdb.Assets;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Cli
{
	// `--report creature-races`: RDB type 1000508 is the client's CREATURE RACE table - the names behind
	// `CharacterStats.race`, which we store as a bare number on every NPC (`NpcStatCore.Race`, `NpcSpawn.Race`).
	//
	// Found while looking for the creature catalog #518 is blocked on. It is NOT that catalog - a race is not a
	// template, and it carries no level, health, faction or name - but it IS the vocabulary the open-world spawn
	// GROUPS are named in, and it is what `SpawnAreaResolver`'s tier 4 resolves an area by.
	//
	// The grammar lives in `CreatureRaceTable`, which also carries the account of how it was decoded WRONG the first
	// time in a way that still parsed to EOF. This report is the other half of that: the checks that would catch it
	// happening again, printed on every run.
	//
	//   1. The record's own arithmetic - parsed entries vs the declared count, and the trailing-byte count.
	//   2. The EXTERNAL join, which is the one that actually falsified the old grammar. A `Race`-category spawn
	//      group carries a numeric race id AND a human-readable name, from different places in the 1010506 record,
	//      so folding the name onto a race and comparing ids is evidence the table itself cannot manufacture.
	//      844/872 agree here against 0/872 under the old reading.
	//
	// It also sizes the #518 gap: how many unresolved spawn groups name a race, and for how many of those we hold a
	// template of that race at all.
	public static partial class ReportCli
	{
		private const uint CreatureRaceType = 1000508;

		private static async Task CreatureRaces()
		{
			using var rdb = OpenRdb();
			if (!rdb.TryGet(CreatureRaceType, 0, out var entry) || !rdb.CanRead(entry))
			{
				Console.WriteLine($"RDB type {CreatureRaceType} (creature races) not found - is RdbDir pointing at the client's RDB folder?");
				return;
			}

			var table = CreatureRaceTable.Parse(rdb.ReadDecompressed(entry));
			var races = table.Races.ToDictionary(r => r.Key, r => r.Value);
			Console.WriteLine($"RDB {CreatureRaceType}: {races.Count} creature races " +
			                  $"(declared {table.DeclaredCount}, {table.TrailingBytes} trailing bytes){(table.Exact ? "  <-- exact" : "  ⚠️  the grammar does not fit")}");
			Console.WriteLine();

			await using var db = OpenDb();
			var held = await db.NpcStatCores.Where(n => n.Race != 0).Select(n => n.Race).Distinct().ToListAsync();
			var heldSet = held.ToHashSet();

			Console.WriteLine("race id -> name (`*` = at least one shipped NpcTemplate has this race):");
			foreach (var (id, name) in races.OrderBy(r => r.Key))
				Console.WriteLine($"  {id,4} {(heldSet.Contains(id) ? "*" : " ")} {name}");

			var absent = races.Where(r => !heldSet.Contains(r.Key)).Select(r => r.Value).Order().ToList();
			Console.WriteLine();
			Console.WriteLine($"races with NO template in the catalog ({absent.Count} of {races.Count}): {string.Join(", ", absent)}");

			// ---- The grammar's own proof (see the header note) ----
			// A `Race`-category spawn group carries a numeric race id (`CommunityId`) AND a human-readable name, and
			// the two come from different places in the 1010506 record. Where the name folds onto a race, the id it
			// declares should BE that race. That is an external join, so it can falsify the grammar - which is exactly
			// what it did to the previous one, where the same table shifted every name onto its neighbour's id.
			var raceGroups = await db
				.NpcSpawnAreas.Where(a => a.Category == "Race" && a.CommunityId != null)
				.Select(a => new { a.GroupName, RaceId = a.CommunityId!.Value })
				.ToListAsync();

			int checkable = 0, agree = 0;
			var disagreements = new List<string>();
			foreach (var g in raceGroups)
			{
				var named = table.MatchRaces(g.GroupName);
				if (named.Count == 0)
					continue;

				checkable++;
				if (named.Contains(g.RaceId))
					agree++;
				else if (disagreements.Count < 12)
					disagreements.Add($"{g.GroupName} declares {g.RaceId} ({races.GetValueOrDefault(g.RaceId, "?")}) but is named " +
					                  $"{string.Join("/", named.Select(n => races.GetValueOrDefault(n, "?")))}");
			}

			Console.WriteLine();
			Console.WriteLine("--- cross-check: a Race group's declared id vs the race its own NAME folds onto ---");
			Console.WriteLine($"  {agree}/{checkable} agree ({100.0 * agree / Math.Max(1, checkable):F1}%) over {raceGroups.Count} Race-category groups");
			foreach (var d in disagreements)
				Console.WriteLine($"    {d}");

			var unresolved = await db.NpcSpawnAreas.Where(a => a.NpcTemplateId == null).Select(a => new { a.MapId, a.GroupName }).ToListAsync();

			var namedTally = new Dictionary<string, int>();
			int matched = 0, withTemplate = 0;
			foreach (var area in unresolved)
			{
				var namedRaces = table.MatchRaces(area.GroupName);
				if (namedRaces.Count == 0)
					continue;

				var raceId = namedRaces.First();

				matched++;
				if (heldSet.Contains(raceId))
					withTemplate++;

				namedTally[races[raceId]] = namedTally.GetValueOrDefault(races[raceId]) + 1;
			}

			Console.WriteLine();
			Console.WriteLine($"unresolved spawn groups: {unresolved.Count}");
			Console.WriteLine($"  name a race:                       {matched} ({100.0 * matched / Math.Max(1, unresolved.Count):F1}%)");
			Console.WriteLine($"  ...and we hold a template of it:   {withTemplate}");
			Console.WriteLine();
			Console.WriteLine("most-named races among the unresolved groups:");
			foreach (var (name, count) in namedTally.OrderByDescending(n => n.Value).Take(25))
				Console.WriteLine($"  {name,-16} {count}");
		}

	}
}
