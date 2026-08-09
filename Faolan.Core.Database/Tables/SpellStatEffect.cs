using System.ComponentModel.DataAnnotations.Schema;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One stat entry a spell carries in its RDB template's [2][9] region - a (kind, wire stat id, amount) record,
	// decoded by the SAME parser as item templates (RdbSpellImporter -> ItemTemplateStatModifiers,
	// docs/rdb/item-template-stats.md, #367). 866 spells carry it (1714 rows).
	//
	// ⚠️ The region is HETEROGENEOUS - NOT a clean "buff = +N stat" list - and EffectKind is what resolves that: the
	// word opening each record is an effect-kind tag, and it says how to read Amount (flat vs percent vs a health
	// effect). See docs/rdb/spell-stat-effects.md for the classification and its evidence. Consequences worth knowing
	// before consuming this table:
	//   - a PERCENT record (kind 53067) stores percent ×100 - Philtre of Guile 0x138 = 200 is "+2% Critical Rating",
	//     not +200;
	//   - a health record (kind 53219/53002/…) is an instant heal or the -100000000 drain/kill sentinel, not a buff;
	//   - attributes on a FLAT record store DISPLAY units (Dexterity Buff 1/2 = 0x32E +1/+2, confirmed by the client's
	//     own description strings), while items store the same attribute ×10 - so ItemStatModifier's GetScale does not
	//     transfer to spell attributes.
	// The server therefore applies only the (kind, stat) pairs it can justify - see BuffStatEffects, which honors flat
	// records on the stats whose wire scale we verified against in-client tooltips, and logs the rest.
	//
	// KEY is the natural (SpellId, StatId): a spell never lists a stat twice (last wire write wins on a repeat).
	// Amount is the RAW template value at whatever scale that stat id / kind uses.
	// ⚠️ The key includes the PERIOD, because a spell states the same stat twice with different cadences and they are
	// different mechanics. `Daggamalt` (Health Potion Effect 10) carries two `Hit` records on stat 27:
	//     0x2d5=1,  0x2d6=0    27 = 40500     405 HP, INSTANTLY
	//     0x2d5=-1, 0x2d6=100  27 = 6750      67.5 HP every 100 cs, for the row's 60 s duration
	// Keyed on (SpellId, StatId) alone the second silently overwrote the first, and which survived depended on how
	// much of the record the element scan could see - so the potion's heal changed from 405 to 67.5 when the scan was
	// fixed (#772), for no reason a player could observe. The spell row corroborates the split independently:
	// DurationMs 60000, DotIntervalMs 1000 (= the 100 cs period), DotDelta 6750.
	[PrimaryKey(nameof(SpellId), nameof(StatId), nameof(PeriodCentiseconds))]
	public class SpellStatEffect
	{
		public uint SpellId { get; set; }

		[ForeignKey(nameof(SpellId))]
		public virtual Spell Spell { get; set; } = null!;

		public CharacterStatId StatId { get; set; }

		// The record's effect-KIND tag, verbatim from the template (the ~53000-range word that opens the record).
		// 0 for a record that starts the body, where the tag would sit before it - unreadable, so never honored.
		public uint EffectKind { get; set; }

		public int Amount { get; set; }

		// How often this record applies, in CENTISECONDS - the element's 0x2d6. **0 = once, instantly.** The same slot
		// that carries an area's aura period (`SpellAreaEffect.PeriodCentiseconds`), which is what makes it a general
		// grammar rather than an area-specific one: `0x2d5 = 1, 0x2d6 = 0` is the instant form and
		// `0x2d5 = -1, 0x2d6 = <period>` the repeating one, on both kinds of element.
		public uint PeriodCentiseconds { get; set; }

		// The LEVEL RAMP the INTERPOLATED kinds carry (53220/53221/53068, Gitea #599), null on every other kind - the
		// same three columns, the same meaning and the same reader as FeatEffect's, because it is the same GameAction
		// element parsed by the same code. `Amount` is then only the ramp's LOW endpoint: the record grants
		// `lerp(Amount@LowLevel, HighAmount@HighLevel)` over the carrier's level, clamped at both ends, so honoring
		// Amount alone under-grants every character above LowLevel. Interpolate through LevelRamp.
		public int? HighAmount { get; set; }

		public uint? LowLevel { get; set; }

		public uint? HighLevel { get; set; }
	}
}
