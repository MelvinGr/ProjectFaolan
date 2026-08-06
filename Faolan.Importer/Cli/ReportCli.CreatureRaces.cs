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
	// Grammar: `[12B RDB-Id header][i32 version][i32 count][count x [i32 nameLength][utf8 name][i32 raceId]]`.
	// ⚠️ The declared count UNDER-reports: the shipped table says 96 and parses cleanly to 108 records, the extra
	// twelve being the late races (Chill Crawler, Crowman, Darfari, Groll, Hyrkanian, Lemurian, Mantis Man,
	// Mountain Man, Vendhyan, Ymirish, ...). Parsing to EOF rather than to the count is what finds them, and it is
	// the same "a count is not a fact" trap the importer's silent-failure note records.
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

			var races = ParseRaces(rdb.ReadDecompressed(entry));
			Console.WriteLine($"RDB {CreatureRaceType}: {races.Count} creature races");
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

		private static Dictionary<uint, string> ParseRaces(byte[] body)
		{
			var races = new Dictionary<uint, string>();
			var offset = 20; // 12-byte RDB-Id header + [i32 version][i32 count]
			while (offset + 4 <= body.Length)
			{
				var length = (int)body.ReadU32Le(offset);
				if (length is <= 0 or > 64 || offset + 4 + length + 4 > body.Length)
					break;

				var name = Encoding.Latin1.GetString(body, offset + 4, length);
				races[body.ReadU32Le(offset + 4 + length)] = name;
				offset += 4 + length + 4;
			}

			return races;
		}

		[GeneratedRegex("[A-Za-z]+")]
		private static partial Regex Words();
	}
}
