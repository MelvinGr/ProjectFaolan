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
			var mods = new List<Modifier>();
			// The full record is [kind][0][4][0][1][0][2][9][statId][amount]; anchor on the 6-word signature
			// (4,0,1,0,2,9) at off..off+20 with the preceding [.. ][0] word, then statId at off+24, amount at off+28.
			// The kind sits two words BEFORE the signature, so it is unreadable for a record that starts the body
			// (off < 8) - those report kind 0, which no policy honors.
			//
			// ⚠️ Scan BYTE by byte, not word by word (#441). The region is NOT 4-byte aligned to the body in most
			// templates - Blackwolf Cap's records start at body offset 297 - and a word-aligned scan simply cannot see
			// those, silently returning zero records. That was 2873 of 3248 templates reporting "no stats" while the
			// client happily applied a full list from the very same bytes. Byte-granular scanning recovers them exactly
			// (Blackwolf Cap 0 -> 4 records, Con 50 / Int 50 = what the client applies) and changes nothing for the
			// templates that were already aligned (Royal Signet stays 3, Fine Steelsilk Pants stays 6).
			for (var off = 4; off + 32 <= body.Length; off++)
			{
				if (W(body, off - 4) != 0 || W(body, off) != 4 || W(body, off + 4) != 0 || W(body, off + 8) != 1 || W(body, off + 12) != 0 || W(body, off + 16) != 2 ||
				    W(body, off + 20) != 9)
					continue;

				var statId = W(body, off + 24);
				if (statId is 0 or >= MaxStatId)
					continue;

				mods.Add(new Modifier(off >= 8 ? W(body, off - 8) : 0, statId, W(body, off + 28)));
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
		public readonly record struct Modifier(uint Kind, uint StatId, uint WireAmount);
	}
}
