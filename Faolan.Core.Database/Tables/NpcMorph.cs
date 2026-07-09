using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Faolan.Core.Database.Tables
{
	// One entry of an NPC TYPE's appearance / morph table (#307/#244) - the type-level counterpart of the per-placement
	// NpcSpawnMorph. A summon-only creature (pet/mount) has no placement, so its appearance lives on the type here and
	// TryBuildFromType generates the post-IIR from these rows (instead of the retired Npc.Data blob). Ordinal preserves
	// the wire order; the texscript entry (id 0x10D) carries a resource string in Text. Cascade-deletes with its Npc.
	public class NpcMorph : BaseEntity
	{
		public uint MorphId { get; set; }

		public uint NpcId { get; set; }

		[ForeignKey(nameof(NpcId))]
		public virtual Npc Npc { get; set; }

		public float Value { get; set; }
		public int Ordinal { get; set; }

		[MaxLength(128)]
		public string Text { get; set; } = "";
	}
}
