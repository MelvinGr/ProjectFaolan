using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Faolan.Core.Extensions;
using Faolan.Core.Rdb;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Cli
{
	// `--report creature-races`: RDB type 1000508 is the client's CREATURE RACE table - the names behind
	// `CharacterStats.race`, which we store as a bare number on every NPC (`NpcStatCore.Race`, `NpcSpawn.Race`).
	//
	// Found while looking for the creature catalog #518 is blocked on. It is NOT that catalog - a race is not a
	// template, and it carries no level, health, faction or name - but it is the vocabulary the open-world spawn
	// GROUPS are named in: RDB 1010506's `Camp18 Mammoths`, `Camp2 Cannibals` and `Camp6 Crag Apes` are races 74,
	// 79 and 72, and those three were the exact examples #518 cited as absent from `NpcTemplate`.
	//
	// So this report answers the question that decides whether a race tier in `SpawnAreaResolver` is worth building:
	// how many unresolved spawn groups name a race, and for how many of those do we already hold a template of that
	// race to stand in for it.
	//
	// Grammar: `[i32 type][i32 id][i32 version][i32 count][count x [i32 raceId][i32 nameLength][utf8 name]]`.
	// The **id PRECEDES its name**, and the header is 16 bytes, not 20.
	//
	// ⚠️ This was decoded wrong the first time, in a way that parsed cleanly and printed a plausible table, so it is
	// worth stating what the failure looked like. Reading the entry as `[nameLength][name][raceId]` from offset 20
	// pairs every name with the id of the entry AFTER it - an off-by-one that shifts the WHOLE table by one row
	// (`Crocodile` took Hyperborian's 9, `Dog` took Living Plant's 66) while still yielding 108 sensible-looking
	// `id -> name` pairs. It also consumed the first entry's raceId (96, Panther's) as the record's COUNT, which is
	// what produced the claim that "the declared count under-reports by 12" and the recommendation to parse to EOF
	// rather than to the count.
	//
	// Read correctly the count is 109, the record parses to EOF in exactly 109 entries with zero trailing bytes, and
	// the ids are a dense 1..109 with no gaps - so the count was a fact all along and the anomaly was ours. The real
	// lesson is the other one: a grammar that parses to EOF is not thereby CORRECT. What settles it is an external
	// join - the 1010506 spawn groups declare a race id beside a human-readable group name, and under this grammar
	// `Badger 1` is race 79 = Badger, `Chicken 1` is 61 = Chicken, `Rabbits` is 72 = Rabbit and `Rats` is 56 = Rat,
	// none of which line up under the old one. The report prints that cross-check on every run (#518).
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

			var (races, declared, trailing) = ParseRaces(rdb.ReadDecompressed(entry));
			Console.WriteLine($"RDB {CreatureRaceType}: {races.Count} creature races " +
			                  $"(declared {declared}, {trailing} trailing bytes){(races.Count == declared && trailing == 0 ? "  <-- exact" : "  ⚠️  the grammar does not fit")}");
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

			// The #518 question. A group name is matched word by word against the race names, singular-folded - the
			// names really are this plain (`Cannibals9`, `Grasslands_Snake06`, `T_Spider_29-1`), which is the point:
			// nothing cleverer is needed to see how far the vocabulary reaches.
			var byName = races.ToDictionary(r => r.Value.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant(), r => r.Key);

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
				if (MatchRace(g.GroupName, byName) is not { } namedRace)
					continue;

				checkable++;
				if (namedRace == g.RaceId)
					agree++;
				else if (disagreements.Count < 12)
					disagreements.Add($"{g.GroupName} declares {g.RaceId} ({races.GetValueOrDefault(g.RaceId, "?")}) but is named {races[namedRace]}");
			}

			Console.WriteLine();
			Console.WriteLine("--- cross-check: a Race group's declared id vs the race its own NAME folds onto ---");
			Console.WriteLine($"  {agree}/{checkable} agree ({100.0 * agree / Math.Max(1, checkable):F1}%) over {raceGroups.Count} Race-category groups");
			foreach (var d in disagreements)
				Console.WriteLine($"    {d}");

			var unresolved = await db.NpcSpawnAreas.Where(a => a.NpcTemplateId == null).Select(a => new { a.MapId, a.GroupName }).ToListAsync();

			var named = new Dictionary<string, int>();
			int matched = 0, withTemplate = 0;
			foreach (var area in unresolved)
			{
				if (MatchRace(area.GroupName, byName) is not { } raceId)
					continue;

				matched++;
				if (heldSet.Contains(raceId))
					withTemplate++;

				named[races[raceId]] = named.GetValueOrDefault(races[raceId]) + 1;
			}

			Console.WriteLine();
			Console.WriteLine($"unresolved spawn groups: {unresolved.Count}");
			Console.WriteLine($"  name a race:                       {matched} ({100.0 * matched / Math.Max(1, unresolved.Count):F1}%)");
			Console.WriteLine($"  ...and we hold a template of it:   {withTemplate}");
			Console.WriteLine();
			Console.WriteLine("most-named races among the unresolved groups:");
			foreach (var (name, count) in named.OrderByDescending(n => n.Value).Take(25))
				Console.WriteLine($"  {name,-16} {count}");
		}

		// The first race name any word of `groupName` folds onto, or null. Words are split on non-letters and a
		// trailing plural `s` is dropped (`Cannibals9` -> `cannibal`).
		private static uint? MatchRace(string groupName, Dictionary<string, uint> byName)
		{
			foreach (var word in Words().Matches(groupName).Select(m => m.Value.ToLowerInvariant()))
			{
				if (byName.TryGetValue(word, out var exact))
					return exact;
				if (word.EndsWith('s') && byName.TryGetValue(word[..^1], out var singular))
					return singular;
			}

			return null;
		}

		// `[i32 type][i32 id][i32 version][i32 count]` then `count x [i32 raceId][i32 nameLength][utf8 name]`.
		// Returns the parsed rows plus what the record CLAIMED and how much was left over, so the caller can print
		// the agreement instead of the reader silently deciding the count was wrong (see the header note).
		private static (Dictionary<uint, string> Races, uint DeclaredCount, int TrailingBytes) ParseRaces(byte[] body)
		{
			var races = new Dictionary<uint, string>();
			var declared = body.Length >= 16 ? body.ReadU32Le(12) : 0;

			var offset = 16; // [type][id][version][count]
			while (offset + 8 <= body.Length)
			{
				var raceId = body.ReadU32Le(offset);
				var length = (int)body.ReadU32Le(offset + 4);
				if (length is <= 0 or > 64 || offset + 8 + length > body.Length)
					break;

				races[raceId] = Encoding.Latin1.GetString(body, offset + 8, length);
				offset += 8 + length;
			}

			return (races, declared, body.Length - offset);
		}

		[GeneratedRegex("[A-Za-z]+")]
		private static partial Regex Words();
	}
}
