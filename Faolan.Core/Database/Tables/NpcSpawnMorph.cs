using System.ComponentModel.DataAnnotations.Schema;

namespace Faolan.Core.Database.Tables
{
	// One entry of a HUMANOID NPC's appearance / morph table (the FA015807 post-IIR), lifted out of the captured
	// spawn so the post-IIR generates from the DB instead of being carried in NpcSpawn.Data (Gitea #185). Ordinal
	// preserves the wire order. Most entries are a plain [morphId][value] morph slider; the texscript entry (id
	// 0x10D) additionally carries a resource string in Text (e.g. "*ii=-1;*bl=-1"). Dropping this table is what
	// rendered humanoids bald (#243). Cascade-deletes with its NpcSpawn. Codec: NpcSpawnAppearanceCodec.
	public class NpcSpawnMorph : BaseEntity
	{
		public uint NpcSpawnId { get; set; }
		public int Ordinal { get; set; }

		public uint MorphId { get; set; }
		public float Value { get; set; }

		// The texscript / resource ref on the entries that carry one (else empty).
		public string Text { get; set; } = "";

		[ForeignKey(nameof(NpcSpawnId))]
		public virtual NpcSpawn NpcSpawn { get; set; }
	}
}
