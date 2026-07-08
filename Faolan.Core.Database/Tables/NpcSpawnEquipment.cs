using System.ComponentModel.DataAnnotations.Schema;

namespace Faolan.Core.Database.Tables
{
	// One worn-equipment entry of an NPC's CharacterIIR (the f23 inventory_entry list), lifted out of the captured
	// spawn so the equipment generates from the DB instead of being parsed from NpcSpawn.Data (Gitea #185, the
	// NPC counterpart of PlayerSpawnEquipment). The wire item is a ProtoAcgItem; the captured NPC items observed
	// follow the same shape the player builder emits - key1==key2==key3 (the 4-fixed32 GUID quad), template1==
	// template2, no template3 - so the byte-exact set to store is the quad + Template + Level + Hash. Cascade-
	// deletes with its NpcSpawn. Codec/build: NpcSpawnEquipmentBuilder.
	public class NpcSpawnEquipment : BaseEntity, INpcEquipment
	{
		[ForeignKey(nameof(NpcSpawnId))]
		public virtual NpcSpawn NpcSpawn { get; set; }

		public uint Hash { get; set; }

		// The item GUID quad (ProtoDecryptionKey, repeated as key1/key2/key3 on the wire).
		public uint Key1 { get; set; }
		public uint Key2 { get; set; }
		public uint Key3 { get; set; }
		public uint Key4 { get; set; }
		public uint Level { get; set; }
		public uint NpcSpawnId { get; set; }

		// The equip-slot id (ProtoCoreCharacterIIR.InventoryEntry.pos), e.g. 18 = wrist.
		public uint Pos { get; set; }

		// ProtoAcgItem.template1 == template2 (fixed32), level (varint), hash (fixed32).
		public uint Template { get; set; }
	}
}
