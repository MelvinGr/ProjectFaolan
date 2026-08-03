using Faolan.Core.Database.Tables;

namespace Faolan.Core.Database.Interfaces
{
	// The stored shape of one worn-equipment entry of an NPC's CharacterIIR (the f23 inventory_entry list),
	// shared by the per-PLACEMENT NpcSpawnEquipment and the per-TYPE NpcEquipment (a summon-only pet/mount carries
	// its equipment on the type, having no placement). NpcSpawnEquipmentBuilder reads/writes through this so one
	// codec serves both. Stores only the equip slot + Template (FK to Items) + Level; the item identity (quad + hash)
	// is resolved through the Item navigation, so the builder needs it Include-loaded (see NpcSpawnEquipment).
	public interface INpcEquipment
	{
		Item Item { get; }
		uint Level { get; set; }
		uint Pos { get; set; }
		uint Template { get; set; }
	}
}
