using System.ComponentModel.DataAnnotations.Schema;

namespace Faolan.Core.Database.Tables
{
	// One pair of the spawn scaffold's two ending [u32 id][s32 value] lists (ids are decades 10..110; only
	// progressed captured characters have any - fresh characters have both lists empty). ListIndex selects the
	// first (0) or second (1) wire list. See docs/archive/player-spawn-generation-plan.md "VALIDATED GRAMMAR".
	public class CharacterScaffoldPair : BaseEntity
	{
		[ForeignKey(nameof(CharacterId))]
		public virtual Character Character { get; set; }

		// 0 = the first ending pair list, 1 = the second.
		public byte ListIndex { get; set; }

		public int Value { get; set; }

		public uint CharacterId { get; set; }

		public uint PairId { get; set; }
	}
}
