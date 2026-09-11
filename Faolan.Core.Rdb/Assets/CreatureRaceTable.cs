using System;
using System.Collections.Generic;
using System.Text;
using Faolan.Core.Extensions;

namespace Faolan.Core.Rdb.Assets
{
	/// <summary>
	///     The client's creature race table (RDB type 1000508, id 0): the 109 names behind <c>CharacterStats.race</c>,
	///     which every NPC carries as a bare number (<c>NpcStatCore.Race</c>, <c>NpcSpawn.Race</c>). This is a
	///     different id space from the three-value player <c>Race</c> enum. See docs/rdb/client-data-tables.md.
	/// </summary>
	/// <remarks>
	///     The grammar is little-endian:
	///     <code>
	///     [u32 type][u32 id][u32 version][u32 count]        count = 109
	///     count * [u32 raceId][u32 nameLength][utf8 name]   the id precedes its name
	///     </code>
	///     It parses to EOF in exactly <c>count</c> entries with zero trailing bytes, and the ids are a dense 1..109
	///     with no gaps.
	///     <para>
	///         A clean termination is not on its own proof of the layout: a window shifted by one field over a uniform
	///         record terminates just as cleanly and yields equally plausible id-to-name pairs. The grammar is instead
	///         corroborated by an external join — a Race-category spawn group in RDB 1010506 declares a race id beside
	///         a human-readable name, and 844 of 872 such groups agree with their own name under this reading.
	///         <c>--report creature-races</c> prints that cross-check on every run.
	///     </para>
	/// </remarks>
	public static class CreatureRaceTable
	{
		public const uint RdbType = 1000508;

		private const int HeaderBytes = 16; // [type][id][version][count]
		private const int MaxNameLength = 64;

		/// <summary>
		///     Parses the decompressed record. The declared count and the trailing byte count are returned rather than
		///     checked here so that the caller can report the agreement; a reader that silently overrides a mismatched
		///     count hides exactly the kind of misread layout described on the type.
		/// </summary>
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

	/// <summary>
	///     The parsed table: <c>Races</c> maps a race id to its name, and <c>Exact</c> is the parse's own verdict —
	///     every declared entry accounted for and nothing left over.
	/// </summary>
	public sealed record CreatureRaces(IReadOnlyDictionary<uint, string> Races, uint DeclaredCount, int TrailingBytes)
	{
		public bool Exact => Races.Count == DeclaredCount && TrailingBytes == 0;

		/// <summary>
		///     Name, lowercased and with spaces stripped, to race id, for folding a spawn group's name onto a race.
		/// </summary>
		public IReadOnlyDictionary<string, uint> ByName { get; } = BuildByName(Races);

		private static Dictionary<string, uint> BuildByName(IReadOnlyDictionary<uint, string> races)
		{
			var byName = new Dictionary<string, uint>();
			foreach (var (id, name) in races)
				byName[name.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant()] = id;

			return byName;
		}

		/// <summary>
		///     Every race any word of <paramref name="text" /> names, empty when it names none. A trailing plural "s"
		///     is dropped, because the group names really are this plain (<c>Cannibals9</c>,
		///     <c>GrasslandsRabbit42</c>, <c>T_Spider_29-1</c>).
		/// </summary>
		/// <remarks>
		///     All matches are returned rather than the first, because a compound routinely opens with an adjective
		///     that is itself a race name: <c>GiantRat01</c> names both Giant (77) and Rat (56), and taking the first
		///     would make it contradict its own declared race 56, blocking 19 perfectly good rat groups. The question a
		///     caller has is whether the name corroborates the declared race, and for that the whole set is the right
		///     answer.
		/// </remarks>
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

		/// <summary>
		///     Whether <paramref name="text" /> names a race other than <paramref name="raceId" /> and does not name
		///     that race itself, which is the name actively contradicting the declared race rather than merely saying
		///     nothing about it, as <c>Territory14</c> does.
		/// </summary>
		public bool ContradictsRace(string text, uint raceId)
		{
			var matched = MatchRaces(text);
			return matched.Count > 0 && !matched.Contains(raceId);
		}

		/// <summary>
		///     The group name's words, lowercased, split on non-letters and on camelCase humps, so
		///     <c>GrasslandsRabbit42</c> yields "grasslands" and "rabbit", as does <c>Grasslands_Fox23</c>.
		/// </summary>
		/// <remarks>
		///     Splitting on boundaries rather than testing every suffix of a letter run is deliberate. Suffixes also
		///     find a race inside a compound, but they fire on coincidences a boundary never does — <c>Landscape</c>
		///     ends in "ape" and <c>Meadow</c> in "cow". The group names are uniformly CamelCase or
		///     separator-delimited (<c>GiantRat01</c>, <c>Rat East 2</c>, <c>Wolverine-8</c>, <c>T_Spider_29-1</c>,
		///     <c>Camp2 Cannibals</c>), so the boundary is always available.
		/// </remarks>
		private static IEnumerable<string> SplitWords(string text)
		{
			var start = -1;
			for (var i = 0; i <= text.Length; i++)
			{
				var isLetter = i < text.Length && char.IsLetter(text[i]);

				// A lower-to-upper transition is a word boundary inside a letter run.
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
