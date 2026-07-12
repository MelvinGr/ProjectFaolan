using System.ComponentModel.DataAnnotations.Schema;

namespace Faolan.Core.Database.Tables
{
	// One worn-equipment entry of an NPC TYPE's CharacterIIR (#307/#244) - the type-level counterpart of the
	// per-placement NpcSpawnEquipment. A summon-only creature (pet/mount) has NO NpcSpawn placement, so its worn
	// equipment lives on the type here and TryBuildFromType builds it into the spawn body from these rows (instead of
	// the retired NpcTemplate.Data blob). Same stored shape as NpcSpawnEquipment (INpcEquipment): slot + Template (FK to
	// Items, the quad + hash live there) + Level; cascade-deletes with its NpcTemplate.
	public class NpcEquipment : BaseEntity, INpcEquipment
	{
		public uint NpcTemplateId { get; set; }

		[ForeignKey(nameof(NpcTemplateId))]
		public virtual NpcTemplate NpcTemplate { get; set; } = null!;

		public uint Level { get; set; }

		// The equip-slot id (ProtoCoreCharacterIIR.InventoryEntry.pos), e.g. 18 = wrist.
		public uint Pos { get; set; }

		// ProtoAcgItem.template1 == template2 (fixed32) = the RDB item template id; hard FK to Items (the quad + hash
		// live there). The builder reads the identity through the Item navigation, so Include it.
		public uint Template { get; set; }

		[ForeignKey(nameof(Template))]
		public virtual Item Item { get; set; } = null!;
	}
}
