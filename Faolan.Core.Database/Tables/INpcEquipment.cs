namespace Faolan.Core.Database.Tables
{
	// The stored shape of one worn-equipment entry of an NPC's CharacterIIR (the f23 inventory_entry list),
	// shared by the per-PLACEMENT NpcSpawnEquipment and the per-TYPE NpcEquipment (a summon-only pet/mount carries
	// its equipment on the type, having no placement). NpcSpawnEquipmentBuilder reads/writes through this so one
	// codec serves both. Fields: the equip slot + the ProtoAcgItem quad/template/level/hash (see NpcSpawnEquipment).
	public interface INpcEquipment
	{
		uint Pos { get; set; }
		uint Key1 { get; set; }
		uint Key2 { get; set; }
		uint Key3 { get; set; }
		uint Key4 { get; set; }
		uint Template { get; set; }
		uint Level { get; set; }
		uint Hash { get; set; }
	}
}
