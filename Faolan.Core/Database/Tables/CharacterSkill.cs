using System.ComponentModel.DataAnnotations.Schema;

namespace Faolan.Core.Database.Tables
{
	// One entry of the spawn scaffold's feat/ability ID list (the 5-byte [u8 rank][u32 id] groups after the
	// entry table, ascending ids, 0x3F1-terminated on the wire). Per-character progression data - fresh
	// characters start with ~20 ids, geared ones have 40+. See docs/player-spawn-generation-plan.md
	// "VALIDATED GRAMMAR" / [[CharacterAttribute]].
	public class CharacterSkill : BaseEntity
	{
		[ForeignKey(nameof(CharacterId))]
		public virtual Character Character { get; set; }

		// The per-id rank/slot byte (0 on fresh characters; geared captures carry values like 0x08/0x41-0x4B).
		public byte Rank { get; set; }

		public uint CharacterId { get; set; }

		// The feat/ability id (wire order is ascending SkillId).
		public uint SkillId { get; set; }
	}
}
