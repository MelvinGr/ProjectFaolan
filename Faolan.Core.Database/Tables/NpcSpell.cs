using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Faolan.Core.Database.Tables
{
	// A spell a (caster-type) NPC casts at its target - the data behind NPC spell-casting (#232). Keyed by the NPC's
	// stable NAME (entity handles are session-volatile, so name is the join key, same as conversations/shops). Lifted
	// from the captures by Faolan.Importer: retail NPC casts are ApplySpell (0x6B87E316) packets whose applyFrom is an
	// NPC handle and applyTo is the player; the importer maps that handle -> the spawn's name and records the spell.
	//
	// Observed: 28 caster NPCs across the beach captures, each casting 1-3 spell bases, every cast arriving as a
	// (base<<8)|rank pair - rank 0 (cast begin) then rank 1 (effect). `SpellId` is the full wire id (base<<8|rank).
	// `Payload` is the captured ApplySpell effect blob (the bytes after applyFrom) kept verbatim for now - the
	// magnitude/duration aren't decoded yet, so the sim replays the captured effect, the same interim the self-buff
	// path uses.
	public class NpcSpell : BaseEntity
	{
		[ForeignKey(nameof(NpcId))]
		public virtual Npc Npc { get; set; }

		// The captured ApplySpell effect payload (bytes after applyFrom) - interim verbatim until decoded.
		public byte[] Payload { get; set; }

		// The casting NPC's name (matched against the live WorldEntity / NpcSpawn name). Retained during the #244
		// expand phase as the live join key; superseded by the NpcId FK below once the loader reads spells via the type.
		[MaxLength(128)]
		public string NpcName { get; set; }

		// The full wire spell id the NPC casts: (rdbBase << 8) | rank. The client resolves the visual/effect from it.
		public uint SpellId { get; set; }

		// The NPC TYPE that casts this spell (#244) - replaces the fragile NpcName string key. Nullable while the
		// expand phase backfills it (by matching NpcName -> Npc.Name); the loader still joins by name until the
		// contract step flips it to the FK.
		public uint? NpcId { get; set; }
	}
}
