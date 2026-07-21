using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Faolan.Core.Rdb
{
	// Parses the item template's stat-modifier region - the full tooltip stat list (attributes, vitals AND ratings),
	// distinct from the strictly-decreasing property table (which only carries the 972 primary attribute, value 74,
	// quality 499, bag 104). See docs/rdb/item-template-stats.md.
	//
	// Each modifier is a fixed 10-word (u32 LE) record, anchored on a 6-word signature:
	//     [typeMarker][0][4][0][1][0][2][9][statId][amount]
	//                     └────── 4,0,1,0,2,9 ──────┘
	// `statId` is a wire stat id (same space as the 0x96B8DC59 vitals / snapshot stat block); `amount` is the bonus at
	// WIRE SCALE (÷100 for vitals + Magic Damage, ÷10 for attributes, ÷1 for ratings - CharacterStatId.GetScale()).
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

		// The wire amount as it sits in the template (NOT yet scaled). Divide by CharacterStatId.GetScale() for display.
		public readonly record struct Modifier(uint StatId, uint WireAmount);

		// All stat-modifier records in a decrypted template BODY (the span AFTER the 12-byte RDB-Id header), in file
		// order. Signature-anchored; statId bounded to the real range. Duplicates are preserved (a template never
		// repeats a stat id in the corpus, but the parser stays faithful to what's on the wire).
		public static IReadOnlyList<Modifier> Parse(ReadOnlySpan<byte> body)
		{
			var mods = new List<Modifier>();
			var words = body.Length / 4;
			// The full record is [typeMarker][0][4][0][1][0][2][9][statId][amount]; anchor on the 6-word signature
			// (4,0,1,0,2,9) at i..i+5 with the preceding [.. ][0] word, then statId at i+6, amount at i+7.
			for (var i = 1; i + 8 <= words; i++)
			{
				if (W(body, i - 1) != 0 || W(body, i) != 4 || W(body, i + 1) != 0 || W(body, i + 2) != 1 || W(body, i + 3) != 0 || W(body, i + 4) != 2 || W(body, i + 5) != 9)
					continue;

				var statId = W(body, i + 6);
				if (statId == 0 || statId >= MaxStatId)
					continue;

				mods.Add(new Modifier(statId, W(body, i + 7)));
			}

			return mods;
		}

		private static uint W(ReadOnlySpan<byte> body, int wordIndex)
		{
			return BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(wordIndex * 4, 4));
		}
	}
}
