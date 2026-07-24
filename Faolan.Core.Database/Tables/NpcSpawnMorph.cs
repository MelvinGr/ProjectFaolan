using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One entry of a HUMANOID NPC's appearance / morph table (the FA015807 post-IIR), lifted out of the captured
	// spawn so the post-IIR generates from the DB instead of being carried in NpcSpawn.Data (Gitea #185). Ordinal
	// preserves the wire order. Most entries are a plain [morphId][value] morph slider; the texscript entry (id
	// 0x10D) additionally carries a resource string in Text (e.g. "*ii=-1;*bl=-1"). Dropping this table is what
	// rendered humanoids bald (#243). Cascade-deletes with its NpcSpawn. Codec: NpcSpawnAppearanceCodec.
	// Keyed by (spawn, Ordinal) - its position in the spawn's morph list, which is the order the wire body carries
	// them in. Rebuilt wholesale with the spawn set on every re-harvest; a surrogate id renumbered all 11658 rows
	// (~30 times over) for no semantic change.
	[PrimaryKey(nameof(NpcSpawnId), nameof(Ordinal))]
	public class NpcSpawnMorph
	{
		public uint MorphId { get; set; }
		public uint NpcSpawnId { get; set; }

		[ForeignKey(nameof(NpcSpawnId))]
		public virtual NpcSpawn NpcSpawn { get; set; } = null!;

		public float Value { get; set; }
		public int Ordinal { get; set; }

		// The texscript / resource ref on the entries that carry one (else empty).
		[MaxLength(128)]
		public string Text { get; set; } = "";
	}
}
