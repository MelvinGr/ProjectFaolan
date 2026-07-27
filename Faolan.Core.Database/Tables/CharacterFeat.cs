using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One unlocked FEAT (talent-tree) the character has trained. This is the FEAT tree, distinct from the
	// ability/skill scaffold (CharacterSkill): feats ride the player spawn's PRE-IIR feat list (a 0x3F1-framed
	// u32 id list), skills ride the POST-IIR id list. Different regions, different id-spaces. Decoded from the
	// captures by tools/capture/decode_unlocked_feats.py; see docs/subsystems/feats.md + [[feat_trees_decode_251]].
	//
	// FeatId is the on-wire composite id. The low two digits track the trained RANK (working hypothesis:
	// FeatId = featBase*100 + rank, e.g. Hellfire base 1127 -> 112700 at rank 0, and hakotep's base 1100 at rank 4
	// = 110004). Stored composite so the wire value is reproduced exactly; the same id-space is used by FeatIIR
	// (0x28DA68A0), the train/unlock record.
	[PrimaryKey(nameof(CharacterId), nameof(FeatId))]
	public class CharacterFeat
	{
		public uint CharacterId { get; set; }

		[ForeignKey(nameof(CharacterId))]
		public virtual Character Character { get; set; } = null!;

		// The composite feat id.
		//
		// ⚠️ NO foreign key to Feat, deliberately: this id space and the RDB catalog's are DISJOINT. Measured against
		// the imported catalog (#251) - 0 of the 56 captured CharacterFeat ids exist in Feats, and neither FeatId/100
		// (the base*100+rank hypothesis) nor FeatId/10 bridges them. A FK here would reject every captured character.
		// The catalog id space IS the one the rest of the client data uses - 45 of the 482 Combo.FeatId values resolve
		// into Feats - so the missing piece is the mapping from the wire's unlocked-feat ids to catalog ids, not the
		// catalog. Add the FK when that mapping exists, not before.
		public uint FeatId { get; set; }

		// Wire position within the pre-IIR feat list. Unlike the ascending skill scaffold, the feat list is in
		// TRAIN order (e.g. NecroL80: 80004, 82704, 410604, 80200, ...), so the order must be preserved to regenerate
		// the spawn byte-exact - hence a Sequence column rather than sorting by id.
		public int Sequence { get; set; }
	}
}
