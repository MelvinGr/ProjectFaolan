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

		// The entry's third word, zero on all but three placements in the whole corpus (#672). It was read as "runtime
		// state" and used to DISQUALIFY a whole appearance table, which cost Turach (template 951), Kincaid (7371) and
		// Valkyrie Skalla (22982) their morphs and spawned them BALD. Three things say it is appearance data:
		//
		//   * it is a dense ORDINAL - the carrying entries are a contiguous PREFIX of the table and their values are a
		//     permutation of 1..N. Runtime state accrued in play would be scattered and arbitrary.
		//     ⚠️ Not SORTED, though: Turach's run is 1/2/3 but Kincaid's is 3/1/2/4, so it does not ordinal the
		//     table's own position - it orders something else.
		//   * ⭐ every entry that carries it is a `*texscript_*` morph - a texture script, never a face/body slider or
		//     a hair mesh (9 of 26,930 shipped rows, all texscripts). Runtime state would not confine itself to one
		//     KIND of entry.
		//   * no single-texscript table ever carries it (0 of 719), which is what an ORDER needs two of.
		//
		// What the order DRIVES is not established, and it is not simply "sent whenever there is more than one
		// texscript" - 505 of the 508 multi-texscript tables send zeros. The column exists so a carrying table
		// round-trips byte-exact rather than being declined wholesale.
		public uint Word2 { get; set; }

		// The texscript / resource ref on the entries that carry one (else empty).
		[MaxLength(128)]
		public string Text { get; set; } = "";
	}
}
