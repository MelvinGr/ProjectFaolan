using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// THE MISSING LINK between the wire's feat ids and the RDB feat catalog (Gitea #467).
	//
	// The two id spaces are disjoint: a character's unlocked feats (CharacterFeat, from the player spawn's pre-IIR
	// list) and the FeatIIR train handshake carry ids like 80004/112700, while the catalog (Feat) is keyed by RDB
	// 1000020 template ids like 7336/115338. Nothing arithmetic bridges them - FeatId/100 does not.
	//
	// The client does not derive it either: it LOADS a table. `FeatTable_t::LoadStaticData()` (`FUN_00788e20`) reads
	// **RDB type 1000042, id 1**, whose per-entry parser (`FUN_00788ef0`) hands (wireId, templateId, +8 fields) to the
	// table builder. That resource is this map: a 20-byte header `[type][id][1][count][version]` then `count` 48-byte
	// entries whose first two words are the pair.
	//
	// It resolves EVERY captured id - 56 of 56 - which is what makes it the answer rather than a lead.
	//
	// ⚠️ A resolved TemplateId is not necessarily a row in Feat: the catalog only holds the ~21% of 1000020 records
	// that are plaintext, and a captured character's feats land largely in the TEA-encrypted remainder (id 1173628 is
	// a real 217-byte 1000020 record we cannot read). So this table deliberately carries NO foreign key to Feat - the
	// mapping is complete, the catalog is not.
	[PrimaryKey(nameof(WireFeatId))]
	public class FeatIdMap
	{
		// The id as it appears ON THE WIRE (CharacterFeat.FeatId, FeatIIR's list).
		public uint WireFeatId { get; set; }

		// The RDB 1000020 template id the client resolves it to - Feat.Id where the template is plaintext.
		public uint TemplateId { get; set; }

		// The entry's 9th word. **1 marks a ROOT feat** - the archetype, class and feat-TREE roots (100 "Core Soldier
		// Knowledge", 1800 "Barbarian", 4100 "Necromancer", "Lich", "Scion of Set", ...), 563 of the 6593 entries.
		// Those are AUTO-GRANTED by the client, which computes ClassAutotrainLevelMap from this table plus the
		// character's class and never receives them on the wire - which is why no captured character carries its own
		// class root in the spawn's feat list.
		//
		// Stored RAW rather than as a bool because the word is not fully decoded: 0 on 4669 entries, 1 on 563,
		// 262144 on 1346 and 2 on 15. Only the ==1 case is understood, so reducing it to IsRoot would throw away the
		// two values we cannot yet explain.
		public uint RootFlag { get; set; }

		// The entry's 11th word, which is exactly WireFeatId / 100 on every row: the client's own base index, and the
		// same "base" the featBase*100+rank hypothesis was reaching for. Carried because it is the client's value,
		// not ours - if it ever diverges from the division, the division was the wrong model.
		public uint BaseIndex { get; set; }

		// The feat's RANK, from the wire id's low two digits (`WireFeatId % 100`) - the other half of the split
		// BaseIndex is the top of. **0-BASED**: the first rank is 0, so "Corrupted Soul" ranks read 84100..84104 as
		// ranks 0..4.
		//
		// ⚠️ This is NOT Feat.Rank, which is parsed from the NAME and is not trustworthy as a rank. The name's
		// trailing number is a rank sometimes ("Thrown Weapons 5") and an INDEX other times ("Guardian Class Feat
		// 011" parses as 11), so the two disagree on 2394 of the 2491 feats where both have a value, and the deltas
		// scatter (+1 x1462, -1 x99, -5 x98, 0 x97, ...) rather than being a clean off-by-one. This one is the
		// client's own encoding - its own loader computes featId/100 for the base - so prefer it.
		//
		// ⚠️ Only a RANK while it is < 10, and that bound is the client's, not a guess: FUN_007850c0 computes
		// `(id / 100) * 100 + 10` and requires `id <` it, so the split it recognises is base*100 + rank with rank in
		// 0..9. 5742 of the 6593 entries fit. The other 851 are all "<Class> Class Feat 071"-style rows whose low
		// digits are an INDEX in a differently-numbered family (24779 reads as 79), the same trap the NAME's trailing
		// number sets. Carried raw with HasRank to gate it, rather than zeroed - the value is real, its meaning just
		// changes above the bound.
		public uint Rank { get; set; }

		// The client's own bound on a feat rank (FUN_007850c0's `base*100 + 10` ceiling).
		public const uint MaxRank = 10;

		// True when Rank really is a rank - i.e. inside the client's own 0..9 window. False on the "Class Feat NNN"
		// rows, where the low digits index a family instead. A convenience over the raw value, not a column.
		[NotMapped]
		public bool HasRank => Rank < MaxRank;

		// True for an archetype/class/tree root - see RootFlag. A convenience over the raw word, not a column.
		[NotMapped]
		public bool IsRoot => RootFlag == RootFlagValue;

		// The RootFlag value that marks a root feat.
		public const uint RootFlagValue = 1;
	}
}
