using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations.Schema;

namespace Faolan.Core.Database.Tables
{
	// One stack of items a character carries that is NOT worn - the bag/backpack/quest/resource contents, as opposed
	// to CharacterEquipment (the worn/visible slots). The FullChar inventory snapshot (0x737A6DF9) is built from these
	// rows; worn items (which also drive the spawn's appearance) stay in CharacterEquipment. Replaces the opaque
	// Bag/Quest/Resources serialized strings on CharacterEquipment with structured rows. See
	// docs/archive/player-spawn-generation-plan.md.
	[PrimaryKey(nameof(CharacterId), nameof(Container), nameof(Slot))]
	public class CharacterInventory
	{
		[ForeignKey(nameof(CharacterId))]
		public virtual Character Character { get; set; }

		// The item definition in this slot (real p_items identity + Data1..4 visual quad). Null = empty slot.
		[ForeignKey(nameof(ItemId))]
		public virtual Item Item { get; set; }

		// Which container this stack lives in (the captured inventory is a nested chain of containers, e.g. the normal
		// backpack vs quest vs resources). Stored as the container's id so the snapshot's container chain can be rebuilt.
		public int Container { get; set; }

		// Position within the container.
		public int Slot { get; set; }

		public uint CharacterId { get; set; }

		// Stack size (1 for non-stackable gear).
		public uint Count { get; set; } = 1;

		public uint? ItemId { get; set; }
	}
}
