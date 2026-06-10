using System.ComponentModel.DataAnnotations.Schema;

namespace Faolan.Core.Database.Tables
{
	// One of a character's skills/feats - an entry in the spawn's IIR skill scaffold (the [skillId][value][weight]
	// table after the ProtoCoreCharacterIIR; weight is the 0x3F800000 = 1.0 marker). Per-character (the cross-capture
	// diff proved this block varies by character), so it must come from the DB to generate the spawn without the
	// template. See docs/player-spawn-generation-plan.md / [[CharacterAttribute]].
	public class CharacterSkill : BaseEntity
	{
		[ForeignKey(nameof(CharacterId))]
		public virtual Character Character { get; set; }

		public uint CharacterId { get; set; }

		// The skill / feat id (the IIR skill key).
		public uint SkillId { get; set; }

		// The skill value / rank.
		public uint Value { get; set; }

		// The per-entry weight (captured as 1.0 / 0x3F800000); kept for byte-faithful regeneration.
		public float Weight { get; set; } = 1.0f;
	}
}
