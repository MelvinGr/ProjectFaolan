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
		public uint FeatId { get; set; }

		// Wire position within the pre-IIR feat list. Unlike the ascending skill scaffold, the feat list is in
		// TRAIN order (e.g. NecroL80: 80004, 82704, 410604, 80200, ...), so the order must be preserved to regenerate
		// the spawn byte-exact - hence a Sequence column rather than sorting by id.
		public int Sequence { get; set; }
	}
}
