using System;
using System.Collections.Generic;
using Faolan.Core.Extensions;

namespace Faolan.Core.Rdb
{
	// Parses the item template's stat-modifier region - the full tooltip stat list (attributes, vitals AND ratings),
	// distinct from the strictly-decreasing property table (which only carries the 972 primary attribute, value 74,
	// quality 499, bag 104). See docs/rdb/item-template-stats.md.
	//
	// Each modifier is a fixed 10-word (u32 LE) record, anchored on a 6-word signature:
	//     [kind][0][4][0][1][0][2][9][statId][amount]
	//               └────── 4,0,1,0,2,9 ──────┘
	// `statId` is a wire stat id (same space as the 0x96B8DC59 vitals / snapshot stat block); `amount` is the bonus at
	// WIRE SCALE (÷100 for vitals + Magic Damage, ÷10 for attributes, ÷1 for ratings - CharacterStatId.GetScale()).
	//
	// `kind` is the word that OPENS the record, and it is what makes the region heterogeneous: it is an effect-KIND tag
	// (a ~53000-range id), not padding. Item templates use one kind throughout, but SPELL templates - parsed by this
	// same code (#367) - mix several, and the kind is what says how to read `amount`:
	//     53045 / 53189 / 53220 / 53221   flat modifier (Battle Cry +62 Hit Rating, Fend +1000 Armor)
	//     53067                           PERCENT modifier, ×100 (Philtre of Guile 200 = +2% Critical Rating)
	//     53219 / 53002 / 53007 / 53013   health effects - heals and the -100000000 drain/kill sentinel
	// So a consumer MUST branch on the kind: reading a 53067 record as flat turns "+2%" into "+200". The classification
	// above is empirical (docs/rdb/spell-stat-effects.md); the server honors only what it can justify - see
	// BuffStatEffects.
	//
	// ANCHOR ON THE SIGNATURE, not a raw [2][9] scan: the raw scan yields ~2500 hits (a third of them coincidental
	// 2,9 word pairs); the signature yields the ~1550 real records (the word before the `4` is 0 in 100% of them).
	// Even so a few signature hits carry an RDB resource-type id as statId (1000017/1000018) or garbage (0x3de071,
	// 0xd00d) - so we bound statId to the real stat-id range (0, 0x1000). Validated 3/3 against the in-client Royal
	// Signet tooltip (+30 Con / +52 Magic Damage / +15 Hit Rating; template 3774542).
	public static class ItemTemplateStatModifiers
	{
		// Stat ids are small (the largest real one is ~0x35d); this bound drops the resource-ref / garbage signature
		// hits without needing a full whitelist here (the importer whitelists to the stats it applies).
		private const uint MaxStatId = 0x1000;

		// All stat-modifier records in a decrypted template BODY (the span AFTER the 12-byte RDB-Id header), in file
		// order. Signature-anchored; statId bounded to the real range. Duplicates are preserved (a template never
		// repeats a stat id in the corpus, but the parser stays faithful to what's on the wire).
		public static IReadOnlyList<Modifier> Parse(ReadOnlySpan<byte> body)
		{
			return Scan(body, true);
		}

		/// <summary>
		///     The records <see cref="Parse" /> REJECTS - those whose payload's first word is outside the stat-id
		///     range. That bound is right for a stat modifier and wrong for the rest: a FEAT's spell modifier puts a
		///     spell-LINE id there and the magnitude second, so `Decomposition` rank 5 reads (41000, 3500) = "+35.00%
		///     on Necromancer line 0, Flesh to Worms" (#590). Same scan, opposite filter - deliberately not a second
		///     copy of the signature walk, so the two can never disagree about where a record IS, only about which
		///     ones count.
		///     <para>
		///         The KIND is what says how to read one; only 53075 is decoded. See
		///         <c>RdbFeatImporter.ParseSpellModifiers</c> and `--report feat-spell-modifiers`.
		///     </para>
		/// </summary>
		public static IReadOnlyList<Modifier> ParseRejected(ReadOnlySpan<byte> body)
		{
			return Scan(body, false);
		}

		/// <summary>
		///     Every GameAction element in the body, whole - kind plus one value per key of that kind's key list.
		///     This is the primitive both readers above are expressed on; use it when a record's later slots matter
		///     (a <c>SpellStatModifier</c> carries spell + stat + value + amount, and the pair readers see only the
		///     first two).
		/// </summary>
		public static IReadOnlyList<Element> ParseElements(ReadOnlySpan<byte> body)
		{
			var elements = new List<Element>();
			// The element is [typeId][field][4][subCount][v(0x2d5)][v(0x2d6)][v(0x2f2)][v(0x2f5)][one u32 per key].
			// In a TEMPLATE those middle six words are always `4,0,1,0,2,9`, which is what makes them usable as an
			// anchor: match them at off..off+20 with the preceding [field][0] word, and the typeId sits two words
			// BEFORE the anchor - so it is unreadable for an element that starts the body (off < 8), and those report
			// kind 0, which no policy honors.
			//
			// ⚠️ Scan BYTE by byte, not word by word (#441). The region is NOT 4-byte aligned to the body in most
			// templates - Blackwolf Cap's records start at body offset 297 - and a word-aligned scan simply cannot see
			// those, silently returning zero records. That was 2873 of 3248 templates reporting "no stats" while the
			// client happily applied a full list from the very same bytes. Byte-granular scanning recovers them exactly
			// (Blackwolf Cap 0 -> 4 records, Con 50 / Int 50 = what the client applies) and changes nothing for the
			// templates that were already aligned (Royal Signet stays 3, Fine Steelsilk Pants stays 6).
			for (var off = 4; off + 24 <= body.Length; off++)
			{
				if (W(body, off - 4) != 0 || W(body, off) != 4 || W(body, off + 4) != 0 || W(body, off + 8) != 1 || W(body, off + 12) != 0 || W(body, off + 16) != 2 ||
				    W(body, off + 20) != 9)
					continue;

				var kind = off >= 8 ? W(body, off - 8) : 0;
				// How many values follow is a property of the KIND, not a constant: an element carries exactly one
				// u32 per key-list entry (docs/re/gameactionlist-grammar.md). An unregistered kind gets the two the
				// old fixed-10-word reader assumed, which is all any caller of the pair readers below ever used.
				var keys = GameActionTypes.Get(kind)?.Keys.Length ?? 2;
				if (off + 24 + 4 * keys > body.Length)
					continue;

				var values = new uint[keys];
				for (var i = 0; i < keys; i++)
					values[i] = W(body, off + 24 + 4 * i);

				elements.Add(new Element(kind, values));
			}

			return elements;
		}

		// One element walk for both pair readers. `inStatRange` picks which side of the stat-id bound to keep.
		private static IReadOnlyList<Modifier> Scan(ReadOnlySpan<byte> body, bool inStatRange)
		{
			var mods = new List<Modifier>();
			foreach (var e in ParseElements(body))
			{
				var statId = e.First;
				if (statId == 0 || statId < MaxStatId != inStatRange)
					continue;

				mods.Add(new Modifier(e.Kind, statId, e.Second));
			}

			return mods;
		}

		// Reads a u32 at a BYTE offset - deliberately not word-indexed, see the scan note in Parse.
		private static uint W(ReadOnlySpan<byte> body, int byteOffset)
		{
			return body.ReadU32Le(byteOffset);
		}

		// One record: its effect KIND (the opening word - see the type comment), the wire stat id, and the amount as it
		// sits in the template (NOT yet scaled). Divide by CharacterStatId.GetScale() for display - after checking that
		// the kind means "flat".
		//
		// ⚠️ This is the PAIR view: the first two payload words, which is (stat, value) for the plain modifier kinds
		// and something else for the rest - a `SpellStatModifier` puts the SPELL first and the stat second. Reach for
		// <see cref="Element" /> when the kind is not known to be a plain modifier.
		public readonly record struct Modifier(uint Kind, uint StatId, uint WireAmount);

		/// <summary>
		///     One whole GameAction element: its type id and one value per key of that type's key list, in key order
		///     (docs/re/gameactionlist-grammar.md). <see cref="Value" /> addresses a slot BY KEY, which is what makes
		///     a reader independent of where the slot sits - the stat is payload word 0 on `Modify` and word 1 on
		///     `SpellStatModifier`.
		/// </summary>
		public readonly record struct Element(uint Kind, uint[] Values)
		{
			/// <summary>The registered type, or null when the kind is not a GameAction the client knows.</summary>
			public GameActionType? Type => GameActionTypes.Get(Kind);

			/// <summary>The value of the slot carrying <paramref name="key" />, or null when this type has no such slot.</summary>
			public uint? Value(int key)
			{
				var slot = Type?.SlotOf(key) ?? -1;
				return slot >= 0 && slot < Values.Length ? Values[slot] : null;
			}

			// The pair view every caller predating the key lists used: the first two payload words. A one-key element
			// has no second word - it used to read the NEXT element's type id, which is how a `Spell` grant came to
			// look like it "repeats its own kind" (#591).
			internal uint First => Values.Length > 0 ? Values[0] : 0;
			internal uint Second => Values.Length > 1 ? Values[1] : 0;
		}
	}
}
