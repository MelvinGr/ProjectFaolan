using System.ComponentModel.DataAnnotations.Schema;
using Faolan.Core.Database.Attributes;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One stack of items a character carries that is NOT worn - the bag/backpack/quest/resource contents, as opposed
	// to CharacterEquipment (the worn/visible slots). The FullChar inventory snapshot (0x737A6DF9) is built from these
	// rows; worn items (which also drive the spawn's appearance) stay in CharacterEquipment. Replaces the opaque
	// Bag/Quest/Resources serialized strings on CharacterEquipment with structured rows. See
	// docs/re/player-spawn-generation.md.
	[PrimaryKey(nameof(CharacterId), nameof(Container), nameof(Slot))]
	public class CharacterInventory // TODO: Rename to CharacterInventorySlot ?
	{
		public uint CharacterId { get; set; }

		[ForeignKey(nameof(CharacterId))]
		public virtual Character Character { get; set; } = null!;

		public uint? ItemId { get; set; }

		// The item definition in this slot (real p_items identity + Data1..4 visual quad). Null = empty slot.
		[ForeignKey(nameof(ItemId))]
		public virtual Item? Item { get; set; }

		// Which container this stack lives in (the captured inventory is a nested chain of containers, e.g. the normal
		// backpack vs quest vs resources). Stored as the container's id so the snapshot's container chain can be rebuilt.
		[ConstrainToEnumValues]
		public ContainerKind Container { get; set; }

		// Position within the container.
		public int Slot { get; set; } // TODO: Change to uint

		// Stack size (1 for non-stackable gear).
		public uint Count { get; set; } = 1;

		// The name of the player who CRAFTED this stack, or null when it was not player-made - the client's
		// "Created by <name>" tooltip line. It is a per-INSTANCE fact, not a template one, which is why it lives on
		// the inventory row rather than on Item.
		//
		// The wire carries it as ProtoInventoryGameEntry.creator_name (field 5, a plain Latin1 string) - NOT as a
		// stat. #727 predicted the carrier was the ItemOrigin stat (0x554) holding the crafter's character id, on
		// the reasoning that a u32 stat cannot hold a name; the crafting capture refutes that. It rides both shapes:
		// the world-entry snapshot (620 sightings across the dumps, 5 distinct crafters) and the live
		// DispatchAddItem (exactly one - the crafted Bloodsilver Khopesh in
		// NecroL80_mage-necromancer_login_gameplay_annotated_5, "NecroL80"). [V2: NecroL80...annotated_5 fr17401]
		public string? CreatorName { get; set; }
	}
}
