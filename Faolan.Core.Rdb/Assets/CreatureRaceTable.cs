using System;
using System.Collections.Generic;
using System.Text;
using Faolan.Core.Extensions;

namespace Faolan.Core.Rdb.Assets
{
	// The client's CREATURE RACE table (RDB type 1000508, id 0) - the 109 names behind `CharacterStats.race`, which
	// every NPC carries as a bare number (`NpcStatCore.Race`, `NpcSpawn.Race`). A different id space from the
	// three-value player `Race` enum.
	//
	// Grammar (little-endian):
	//   [u32 type][u32 id][u32 version][u32 count]        count = 109
	//   count * [u32 raceId][u32 nameLength][utf8 name]   <- the id PRECEDES its name
	//
	// Parses to EOF in exactly `count` entries with zero trailing bytes, and the ids are a dense 1..109 with no gaps.
	//
	// ⚠️ The first decode of this read the entry as `[nameLength][name][raceId]` from offset 20, which ALSO ran to EOF
	// and ALSO produced 108 plausible id->name pairs - it simply paired every name with the id of the entry after it,
	// shifting the whole table by one row, and consumed the first entry's raceId (96, Panther's) as the record's
	// count. A grammar that terminates cleanly is not thereby correct; a shifted window over a uniform record
	// terminates too. What falsified it was an external join - a `Race`-category spawn group in RDB 1010506 declares a
	// race id beside a human-readable name, and under this grammar 844 of 872 such groups agree with their own name
	// against 0 of 872 under the old one. `--report creature-races` prints that cross-check on every run.
	//
	// See docs/rdb/client-data-tables.md.
	public static class CreatureRaceTable
	{
		public const uint RdbType = 1000508;

		private const int HeaderBytes = 16; // [type][id][version][count]
		private const int MaxNameLength = 64;

		// Parse the decompressed record. `DeclaredCount` and `TrailingBytes` are returned rather than checked here so
		// the caller can PRINT the agreement - a reader that silently decides the count was wrong is how the previous
		// grammar survived (see the note above).
		public static CreatureRaces Parse(ReadOnlySpan<byte> body)
		{
			var races = new Dictionary<uint, string>();
			var declared = body.Length >= HeaderBytes ? body.ReadU32Le(12) : 0;

			var offset = HeaderBytes;
			while (offset + 8 <= body.Length)
			{
				var raceId = body.ReadU32Le(offset);
				var length = (int)body.ReadU32Le(offset + 4);
				if (length is <= 0 or > MaxNameLength || offset + 8 + length > body.Length)
					break;

				races[raceId] = Encoding.Latin1.GetString(body.Slice(offset + 8, length));
				offset += 8 + length;
			}

			return new CreatureRaces(races, declared, body.Length - offset);
		}
	}

	// `Races` is raceId -> name. `Exact` is the parse's own verdict: every declared entry accounted for and nothing
	// left over.
	public sealed record CreatureRaces(IReadOnlyDictionary<uint, string> Races, uint DeclaredCount, int TrailingBytes)
	{
		public bool Exact => Races.Count == DeclaredCount && TrailingBytes == 0;

		// name (lowercased, spaces stripped) -> raceId, for folding a spawn group's name onto a race.
		public IReadOnlyDictionary<string, uint> ByName { get; } = BuildByName(Races);

		private static Dictionary<string, uint> BuildByName(IReadOnlyDictionary<uint, string> races)
		{
			var byName = new Dictionary<string, uint>();
			foreach (var (id, name) in races)
				byName[name.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant()] = id;

			return byName;
		}

		// EVERY race any word of `text` names, which is empty when it names none. A trailing plural `s` is dropped,
		// because the group names really are this plain (`Cannibals9`, `GrasslandsRabbit42`, `T_Spider_29-1`).
		//
		// ⚠️ All of them, not the first: a compound routinely opens with an adjective that is itself a race name.
		// `GiantRat01` names Giant (77) AND Rat (56), and taking the first would make it contradict its own declared
		// race 56 - blocking 19 perfectly good rat groups. The question a caller actually has is "does this name
		// CORROBORATE the declared race", and for that the whole set is the right answer.
		public IReadOnlySet<uint> MatchRaces(string text)
		{
			var matched = new HashSet<uint>();
			foreach (var word in SplitWords(text))
			{
				if (ByName.TryGetValue(word, out var exact))
					matched.Add(exact);
				else if (word.EndsWith('s') && ByName.TryGetValue(word[..^1], out var singular))
					matched.Add(singular);
			}

			return matched;
		}

		// Whether `text` names a race OTHER than `raceId` and does not name `raceId` itself - i.e. the name actively
		// CONTRADICTS the declared race, as opposed to merely saying nothing about it (`Territory14`).
		public bool ContradictsRace(string text, uint raceId)
		{
			var matched = MatchRaces(text);
			return matched.Count > 0 && !matched.Contains(raceId);
		}

		// The group name's words, lowercased: split on non-letters AND on camelCase humps, so `GrasslandsRabbit42`
		// yields `grasslands`, `rabbit` and `Grasslands_Fox23` yields `grasslands`, `fox`.
		//
		// ⚠️ Deliberately NOT "every suffix of the run". That also finds the race inside a compound, but it fires on
		// coincidences a boundary never does - `Landscape` would end in `ape` and `Meadow` in `cow`. The group names
		// are uniformly CamelCase or separator-delimited (`GiantRat01`, `Rat East 2`, `Wolverine-8`, `T_Spider_29-1`,
		// `Camp2 Cannibals`), so the boundary is available and there is no reason to guess past it.
		private static IEnumerable<string> SplitWords(string text)
		{
			var start = -1;
			for (var i = 0; i <= text.Length; i++)
			{
				var isLetter = i < text.Length && char.IsLetter(text[i]);

				// A lower->upper transition is a word boundary inside a letter run.
				var isHump = isLetter && start >= 0 && char.IsUpper(text[i]) && char.IsLower(text[i - 1]);
				if (isHump)
				{
					yield return text[start..i].ToLowerInvariant();
					start = i;
					continue;
				}

				if (isLetter && start < 0)
					start = i;
				else if (!isLetter && start >= 0)
				{
					yield return text[start..i].ToLowerInvariant();
					start = -1;
				}
			}
		}
	}
}
