using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One unlocked feat (talent-tree) the character has trained. This is the feat tree, distinct from the
	// ability/skill scaffold (CharacterSkill): feats ride the player spawn's pre-IIR feat list (a 0x3F1-framed
	// u32 id list), skills ride the post-IIR id list. Different regions, different id-spaces. Decoded from the
	// captures by tools/capture/decode_unlocked_feats.py; see docs/subsystems/feats.md + [[feat_trees_decode_251]].
	//
	// FeatId is the on-wire composite id. The low two digits track the trained rank (working hypothesis:
	// FeatId = featBase*100 + rank, e.g. Hellfire base 1127 -> 112700 at rank 0, and hakotep's base 1100 at rank 4
	// = 110004). Stored composite so the wire value is reproduced exactly; the same id-space is used by FeatIIR
	// (0x28DA68A0), the train/unlock record.
	// Keyed by the spec as well as the feat: a character may hold several specs (CharacterFeatSpec) and the same feat
	// id legitimately appears in more than one of them - four of the captured multi-spec characters share most of
	// their list across specs. A (CharacterId, FeatId) key would silently collapse those into one.
	[PrimaryKey(nameof(CharacterId), nameof(SpecIndex), nameof(FeatId))]
	public class CharacterFeat
	{
		public uint CharacterId { get; set; }

		[ForeignKey(nameof(CharacterId))]
		public virtual Character Character { get; set; } = null!;

		// Which of the character's specs this feat is trained in. 0 for a single-spec character, which is every
		// character we ship - so this defaults to the historical behaviour rather than needing a backfill.
		public int SpecIndex { get; set; }

		[ForeignKey($"{nameof(CharacterId)},{nameof(SpecIndex)}")]
		public virtual CharacterFeatSpec Spec { get; set; } = null!;

		// The composite feat id.
		//
		// No foreign key to Feat, deliberately: this id space and the RDB catalog's are disjoint. Measured against
		// the imported catalog - 0 of the 56 captured CharacterFeat ids exist in Feats, and neither FeatId/100
		// (the base*100+rank hypothesis) nor FeatId/10 bridges them. A FK here would reject every captured character.
		// The catalog id space is the one the rest of the client data uses - 45 of the 482 Combo.FeatId values resolve
		// into Feats - so the missing piece is the mapping from the wire's unlocked-feat ids to catalog ids, not the
		// catalog. Add the FK when that mapping exists, not before.
		public uint FeatId { get; set; }

		// Wire position within its spec's feat list. Unlike the ascending skill scaffold, the feat list is in
		// train order (e.g. NecroL80: 80004, 82704, 410604, 80200, ...), so the order must be preserved to regenerate
		// the spawn byte-exact - hence a Sequence column rather than sorting by id.
		public int Sequence { get; set; }
	}
}
