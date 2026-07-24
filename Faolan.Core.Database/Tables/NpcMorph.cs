using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One entry of an NPC TYPE's appearance / morph table (#307/#244) - the type-level counterpart of the per-placement
	// NpcSpawnMorph. A summon-only creature (pet/mount) has no placement, so its appearance lives on the type here and
	// TryBuildFromType generates the post-IIR from these rows (instead of the retired NpcTemplate.Data blob). Ordinal preserves
	// the wire order; the texscript entry (id 0x10D) carries a resource string in Text. Cascade-deletes with its NpcTemplate.
	// Keyed by (type, Ordinal) - its position in the type's morph list, like its NpcSpawnMorph twin.
	[PrimaryKey(nameof(NpcTemplateId), nameof(Ordinal))]
	public class NpcMorph
	{
		public uint MorphId { get; set; }

		public uint NpcTemplateId { get; set; }

		[ForeignKey(nameof(NpcTemplateId))]
		public virtual NpcTemplate NpcTemplate { get; set; } = null!;

		public float Value { get; set; }
		public int Ordinal { get; set; }

		[MaxLength(128)]
		public string Text { get; set; } = "";
	}
}
