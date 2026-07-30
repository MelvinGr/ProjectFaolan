using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// A spell a (caster-type) NPC casts at its target - the data behind NPC spell-casting (#232). Keyed by the NPC
	// TYPE (NpcTemplateId FK). Lifted from the captures by Faolan.Importer: retail NPC casts are ApplySpell (0x6B87E316)
	// packets whose applyFrom is an NPC handle and applyTo is the player; the importer maps that handle -> the spawn's
	// name -> its NpcTemplate type, and records the spell + decoded effect under NpcTemplateId (unlinkable casters are dropped).
	//
	// Observed: 28 caster NPCs across the beach captures, each casting 1-3 spell bases, every cast arriving as a
	// (base<<8)|rank pair - rank 0 (cast begin) then rank 1 (effect). `SpellId` is the full wire id (base<<8|rank).
	//
	// The 54-byte ApplySpell effect payload is now DECODED into columns (#304, NpcSpellEffectCodec): the sim BUILDS
	// the wire payload from these instead of replaying a stored blob. See NpcSpellEffect for the byte layout.
	// Keyed by (caster TYPE, wire spell id) - one row per spell a type casts, which is what the importer already
	// deduped on. A natural key so a re-harvest doesn't renumber the table (see ConversationNode).
	[PrimaryKey(nameof(NpcTemplateId), nameof(SpellId))]
	public class NpcSpell
	{
		// The NPC TYPE that casts this spell (#244, contract step done): the loader reads spells via this FK
		// (NpcTemplate.Spells), so a spell whose caster has no NpcTemplate type isn't stored. Resolved from the caster name at import.
		public uint NpcTemplateId { get; set; }

		[ForeignKey(nameof(NpcTemplateId))]
		public virtual NpcTemplate NpcTemplate { get; set; } = null!;

		// The full wire spell id the NPC casts: (rdbBase << 8) | rank. The client resolves the visual/effect from it.
		public uint SpellId { get; set; }

		// The ApplySpell payload's stored bytes. ⚠️ These are LOW BYTES of wider fields the client reads, and they were
		// named from diffing captured payloads before the client's own reader was walked (#304 -> #438/#480). Every
		// offset written is still the offset retail writes - byte-exactness never depended on the names - but the old
		// names invited wrong reasoning, so they now say what the client says. Full table:
		// docs/subsystems/npc-spell-applyspell.md.
		//
		//   was `FlagB`     @17  the low byte of descriptor slot [0]
		//   was `Magnitude` @29  the low byte of the u32 at @26 = descriptor slot [3]  - NOT a magnitude
		//   was `Rank`      @45  the low byte of the u32 CastingLevel at @42            - NOT a rank
		//
		// The @50 "Counter" is gone entirely: it was the LOW HALF of a u64 absolute expiry stamp at @46, which is
		// session state rather than per-type content and is regenerated per send (see SelfBuffCodec / #480).
		public byte FlagA { get; set; }

		// @17 - the low byte of descriptor slot [0].
		public byte DescriptorSlot0 { get; set; }

		// @33 - the low byte of descriptor slot [8].
		public byte DescriptorSlot8 { get; set; }

		// @29 - the low byte of descriptor slot [3], which sits beside the spell id. ⚠️ [V0] What feeds it is still
		// open: it tracks CastingLevel closely but not exactly (1563 equal / 283 below / 92 above across the corpus),
		// so a second input exists. What IS settled is that it is not a magnitude and not a stack count.
		public byte DescriptorSlot3 { get; set; }

		// @45 - the low byte of the CASTER's level (the u32 CastingLevel at @42).
		public byte CastingLevel { get; set; }

		// @8 (its low half at @10) - centiseconds; the client multiplies by 10 for milliseconds.
		public ushort Duration { get; set; }
	}
}
