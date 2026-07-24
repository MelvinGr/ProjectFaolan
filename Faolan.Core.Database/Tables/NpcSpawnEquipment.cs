using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One worn-equipment entry of an NPC's CharacterIIR (the f23 inventory_entry list), lifted out of the captured
	// spawn so the equipment generates from the DB instead of being parsed from NpcSpawn.Data (Gitea #185, the
	// NPC counterpart of PlayerSpawnEquipment). The wire item is a ProtoAcgItem; the captured NPC items observed
	// follow the same shape the player builder emits - key1==key2==key3 (the 4-fixed32 quad), template1==template2,
	// no template3 - and that quad+hash is TEMPLATE-level identity, so it lives ONCE in Items and this row references
	// it by an FK on Template (the item is always harvested from the same spawn packet). Stores only the slot +
	// Template + Level. Cascade-deletes with its NpcSpawn. Codec/build: NpcSpawnEquipmentBuilder.
	// Keyed by (spawn, equipment SLOT) - a spawn wears one item per slot, so Pos is the identity within its spawn.
	// The npcs module wipes and rebuilds the whole spawn set on every re-harvest; with a surrogate id that
	// renumbered all 5973 rows (~37 times over), which is the single largest source of faolan.sql churn.
	[PrimaryKey(nameof(NpcSpawnId), nameof(Pos))]
	public class NpcSpawnEquipment : INpcEquipment
	{
		[ForeignKey(nameof(Template))]
		public virtual Item Item { get; set; } = null!;

		public uint NpcSpawnId { get; set; }

		[ForeignKey(nameof(NpcSpawnId))]
		public virtual NpcSpawn NpcSpawn { get; set; } = null!;

		public uint Level { get; set; }

		// The equip-slot id (ProtoCoreCharacterIIR.InventoryEntry.pos), e.g. 18 = wrist.
		public uint Pos { get; set; }

		// ProtoAcgItem.template1 == template2 (fixed32) = the RDB item template id; hard FK to Items (the quad + hash
		// live there). The builder reads the identity through the Item navigation, so Include it.
		public uint Template { get; set; }
	}
}
