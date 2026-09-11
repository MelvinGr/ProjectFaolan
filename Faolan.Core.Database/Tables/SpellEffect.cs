using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Faolan.Core.Database.Attributes;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// What a spell does beyond landing a buff icon - the effects the server has to act on itself, as data rather than
	// as a spell id hardcoded into a dispatch chain.
	//
	// The generic wire path (ApplySpell + the RDB duration + SpellStatEffect's stat deltas) covers a buff that only
	// modifies its carrier. A handful of spells do something the wire cannot express: they summon a creature, or they
	// arm a proc that fires on a later event. Those were six id-keyed `if`s scattered across the listener, the cast
	// resolve and FatalityService; this table is where they live now, so a new one is an imported row, not a new
	// branch.
	//
	// Not the same thing as SpellStatEffect, and deliberately not merged with it. That table is the client's own
	// [2][9] template region, machine-extracted per spell (1714 rows over 866 spells) and re-derived wholesale on
	// every RDB re-import - which is why it is keyed by the identity the region gives it, (SpellId, StatId). This one
	// is a few dozen rows of decoded behaviour that appears nowhere in the spell template: no field of the necro's
	// summon record names the creature it spawns, and no field of Frozen Hatred says it procs on a fatality - the
	// captures do. Folding them would put an extracted region and a hand-decoded action list under one key, and give
	// the extracted half a sequence number that renumbers on every re-harvest.
	//
	// Key is the natural (SpellId, Trigger, Sequence): one spell can carry several effects on the same trigger - the
	// Frozen Hatred fatality proc applies a buff and summons a pet - and Sequence is the order they run in.
	//
	// SpellId is an FK to the catalog, which makes seeding an effect for a spell the catalog does not hold a failure
	// rather than an orphan row pointing at nothing: the server resolves the spell for its name and duration on the
	// very path the effect fires from.
	[PrimaryKey(nameof(SpellId), nameof(Trigger), nameof(Sequence))]
	public class SpellEffect
	{
		// The base spell id (the Spells key convention), never the wire form (base << 8) | rank. For OnCast this is
		// the id in the cast trailer; for OnFatality it is the base of the buff that must be live on the killer.
		public uint SpellId { get; set; }

		[ForeignKey(nameof(SpellId))]
		public virtual Spell Spell { get; set; } = null!;

		[ConstrainToEnumValues]
		public SpellEffectTrigger Trigger { get; set; }

		// Run order within one (SpellId, Trigger). Also what lets a spell carry two effects of the same kind.
		public byte Sequence { get; set; }

		[ConstrainToEnumValues]
		public SpellEffectKind Kind { get; set; }

		// ApplySpell: the base id of the spell to apply to the player.
		public uint? TargetSpellId { get; set; }

		// SummonPet: the creature type name (NpcTemplate.Name) to spawn - the stable key a summon resolves by,
		// since a pet has no world placement and no session-stable handle.
		[MaxLength(128)]
		public string? Template { get; set; }

		// SummonPet: the PetIIR kind triple - a combat pet (targetable, takes commands) vs a social companion.
		public bool Combat { get; set; }

		// SummonPet: how long the pet lives. Null = until dismissed (the necro's own pets); set = a timed summon that
		// the world tick fades when it runs out.
		public uint? DurationMs { get; set; }

		// SummonPet: the ceiling a re-summon may extend a timed pet to. Null = no cap. Frozen Hatred's tooltip states
		// both halves - "the duration of the pet will increase by another 60 seconds. This timer is capped at 5
		// minutes."
		public uint? MaxDurationMs { get; set; }
	}
}
