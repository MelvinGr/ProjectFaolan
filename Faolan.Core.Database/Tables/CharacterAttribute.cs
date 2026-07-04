using System.ComponentModel.DataAnnotations.Schema;

namespace Faolan.Core.Database.Tables
{
	// One row of the spawn scaffold's uniform entry table (the count-prefixed 14-byte records after the
	// ProtoCoreCharacterIIR): [u32 EntryIndex][u32 0][u16 Tag][u32 Value]. The low indexes are the weighted
	// attributes (Tag = the attribute value, Value = 0x3F800000 weight 1.0), the higher ones the morph/feat
	// entries (Tag = morph/slider id, Value = float bits) and the closing [u16][u32] record. Wire order is
	// Sequence, NOT StatId - captures exist where the closing record's index is lower than its predecessor's.
	// See docs/archive/player-spawn-generation-plan.md "VALIDATED GRAMMAR" / [[CharacterSkill]].
	public class CharacterAttribute : BaseEntity
	{
		[ForeignKey(nameof(CharacterId))]
		public virtual Character Character { get; set; }

		// The entry's position in the wire table.
		public int Sequence { get; set; }

		public uint CharacterId { get; set; }

		// The entry's index value carried IN the wire record (usually but not always ascending, 1-based). NOT a stat
		// id despite the field's old name - the wire order is Sequence, this is the index written inside each entry.
		public uint EntryIndex { get; set; }

		// The last four payload bytes (weight 1.0 for attributes, float bits for morphs, the closer's u32).
		public uint Value { get; set; }

		// The first two payload bytes (attribute value / morph id / the closer's u16).
		public ushort Tag { get; set; }
	}
}
