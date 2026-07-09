using System.ComponentModel.DataAnnotations.Schema;

namespace Faolan.Core.Database.Tables
{
	// One worn-equipment entry of an NPC TYPE's CharacterIIR (#307/#244) - the type-level counterpart of the
	// per-placement NpcSpawnEquipment. A summon-only creature (pet/mount) has NO NpcSpawn placement, so its worn
	// equipment lives on the type here and TryBuildFromType builds it into the spawn body from these rows (instead of
	// the retired Npc.Data blob). Same stored shape as NpcSpawnEquipment (INpcEquipment); cascade-deletes with its Npc.
	public class NpcEquipment : BaseEntity, INpcEquipment
	{
		public uint NpcId { get; set; }

		[ForeignKey(nameof(NpcId))]
		public virtual Npc Npc { get; set; }

		public uint Hash { get; set; }
		public uint Key1 { get; set; }
		public uint Key2 { get; set; }
		public uint Key3 { get; set; }
		public uint Key4 { get; set; }
		public uint Level { get; set; }

		// The equip-slot id (ProtoCoreCharacterIIR.InventoryEntry.pos), e.g. 18 = wrist.
		public uint Pos { get; set; }

		// ProtoAcgItem.template1 == template2 (fixed32).
		public uint Template { get; set; }
	}
}
