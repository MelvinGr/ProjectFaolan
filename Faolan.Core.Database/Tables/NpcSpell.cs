using System.ComponentModel.DataAnnotations.Schema;

namespace Faolan.Core.Database.Tables
{
	// A spell a (caster-type) NPC casts at its target - the data behind NPC spell-casting (#232). Keyed by the NPC
	// TYPE (NpcId FK). Lifted from the captures by Faolan.Importer: retail NPC casts are ApplySpell (0x6B87E316)
	// packets whose applyFrom is an NPC handle and applyTo is the player; the importer maps that handle -> the spawn's
	// name -> its Npc type, and records the spell + decoded effect under NpcId (unlinkable casters are dropped).
	//
	// Observed: 28 caster NPCs across the beach captures, each casting 1-3 spell bases, every cast arriving as a
	// (base<<8)|rank pair - rank 0 (cast begin) then rank 1 (effect). `SpellId` is the full wire id (base<<8|rank).
	//
	// The 54-byte ApplySpell effect payload is now DECODED into columns (#304, NpcSpellEffectCodec): the sim BUILDS
	// the wire payload from these instead of replaying a stored blob. See NpcSpellEffect for the byte layout.
	public class NpcSpell : BaseEntity
	{
		// The NPC TYPE that casts this spell (#244, contract step done): the loader reads spells via this FK
		// (Npc.Spells), so a spell whose caster has no Npc type isn't stored. Resolved from the caster name at import.
		public uint NpcId { get; set; }

		[ForeignKey(nameof(NpcId))]
		public virtual Npc Npc { get; set; } = null!;

		// The full wire spell id the NPC casts: (rdbBase << 8) | rank. The client resolves the visual/effect from it.
		public uint SpellId { get; set; }

		public byte FlagA { get; set; }
		public byte FlagB { get; set; }
		public byte FlagC { get; set; }
		public byte Magnitude { get; set; }
		public byte Rank { get; set; }

		// Decoded ApplySpell effect fields (payload @10 / @29 / @45 / @13 / @17 / @33). The effect spell id (@23) is the
		// same as SpellId, and the @50 per-cast counter is session state (not stored) - both fall out of the builder.
		public ushort Duration { get; set; }
	}
}
