using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// A spell a (caster-type) NPC casts at its target - the data behind NPC spell-casting. Keyed by the NPC
	// type (NpcTemplateId FK). Lifted from the captures by Faolan.Importer: retail NPC casts are ApplySpell (0x6B87E316)
	// packets whose applyFrom is an NPC handle and applyTo is the player; the importer maps that handle -> the spawn's
	// name -> its NpcTemplate type, and records the spell + decoded effect under NpcTemplateId (unlinkable casters are dropped).
	//
	// Observed: 28 caster NPCs across the beach captures, each casting 1-3 spell bases, every cast arriving as a
	// (base<<8)|rank pair - rank 0 (cast begin) then rank 1 (effect). `SpellId` is the full wire id (base<<8|rank).
	//
	// The 54-byte ApplySpell effect payload is decoded into columns (NpcSpellEffectCodec): the sim builds
	// the wire payload from these instead of replaying a stored blob. See NpcSpellEffect for the byte layout.
	// Keyed by (caster type, wire spell id) - one row per spell a type casts, which is what the importer already
	// deduped on. A natural key so a re-harvest doesn't renumber the table (see ConversationNode).
	[PrimaryKey(nameof(NpcTemplateId), nameof(SpellId))]
	public class NpcSpell
	{
		// The NPC type that casts this spell: the loader reads spells via this FK
		// (NpcTemplate.Spells), so a spell whose caster has no NpcTemplate type isn't stored. Resolved from the caster name at import.
		public uint NpcTemplateId { get; set; }

		[ForeignKey(nameof(NpcTemplateId))]
		public virtual NpcTemplate NpcTemplate { get; set; } = null!;

		// The full wire spell id the NPC casts: (rdbBase << 8) | rank. The client resolves the visual/effect from it.
		public uint SpellId { get; set; }

		// The ApplySpell payload's stored bytes. Each is the low byte of a wider field the client reads, named after
		// what the client's own reader calls it - a name that describes the byte in isolation (a "magnitude", a
		// "rank") invites wrong reasoning about a field that has neither meaning. Full table:
		// docs/subsystems/npc-spell-applyspell.md.
		//
		//   @17  the low byte of descriptor slot [0]
		//   @29  the low byte of the u32 at @26 = descriptor slot [3]
		//   @45  the low byte of the u32 CastingLevel at @42
		//
		// The u64 absolute expiry stamp at @46 is not stored at all: it is session state rather than per-type
		// content, and is regenerated per send (see SelfBuffCodec).
		public byte FlagA { get; set; }

		// @17 - the low byte of descriptor slot [0].
		public byte DescriptorSlot0 { get; set; }

		// @33 - the low byte of descriptor slot [8].
		public byte DescriptorSlot8 { get; set; }

		// @29 - the low byte of descriptor slot [3], which sits beside the spell id. [V0] What feeds it is still
		// open: it tracks CastingLevel closely but not exactly (1563 equal / 283 below / 92 above across the corpus),
		// so a second input exists. What is settled is that it is not a magnitude and not a stack count.
		public byte DescriptorSlot3 { get; set; }

		// @45 - the low byte of the caster's level (the u32 CastingLevel at @42).
		public byte CastingLevel { get; set; }

		// @8 (its low half at @10) - centiseconds; the client multiplies by 10 for milliseconds.
		public ushort Duration { get; set; }
	}
}
