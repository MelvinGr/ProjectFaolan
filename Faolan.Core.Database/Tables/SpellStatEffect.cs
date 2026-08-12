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
	// KEY is the natural (SpellId, Sequence) - one row per ELEMENT, in the order the record states them, so a
	// re-harvest of the same record reproduces the same ids rather than churning a surrogate (the model conventions in
	// CLAUDE.md). Amount is the RAW template value at whatever scale that stat id / kind uses.
	//
	// ⚠️ It used to be (SpellId, StatId, PeriodCentiseconds), and that key was LOSSY: a spell routinely states several
	// elements on the same stat AND period, and every one but the last was discarded (#783). Two shapes, both shipped:
	//   * LEVEL BANDS - `Hellfire Stream` (8600) carries five `InterpolatedHit` elements, one per band
	//     (1-5) (6-10) (11-15) (16-20) (21-30), all on (stat 27, period 0). One row survived, so a level-1 caster and
	//     a level-30 caster got the same number and it was the wrong one for almost everyone.
	//   * PLAIN REPEATS - `Life Leech`'s effect record (220980) carries four heals and one drain on stat 27. Two rows
	//     survived: its instant heal read 106.93 instead of 42.77 + 106.93, and its ticking heal was replaced by the
	//     drain outright.
	// A consumer therefore has to RESOLVE the set rather than expect one row per stat - see `SpellEffectBands`, which
	// picks the band covering the carrier's level and sums what is left.
	//
	// The PERIOD is still carried, and is still what separates two mechanics on one stat. `Daggamalt` (Health Potion
	// Effect 10) carries two `Hit` records on stat 27:
	//     0x2d5=1,  0x2d6=0    27 = 40500     405 HP, INSTANTLY
	//     0x2d5=-1, 0x2d6=100  27 = 6750      67.5 HP every 100 cs, for the row's 60 s duration
	// Under the older (SpellId, StatId) key the second silently overwrote the first, and which survived depended on
	// how much of the record the element scan could see - so the potion's heal changed from 405 to 67.5 when the scan
	// was fixed (#772), for no reason a player could observe. The spell row corroborates the split independently:
	// DurationMs 60000, DotIntervalMs 1000 (= the 100 cs period), DotDelta 6750.
	[PrimaryKey(nameof(SpellId), nameof(Sequence))]
	public class SpellStatEffect
	{
		public uint SpellId { get; set; }

		[ForeignKey(nameof(SpellId))]
		public virtual Spell Spell { get; set; } = null!;

		// The effect's ordinal within the record's stat-effect list - element order, which is byte order in the
		// template, so it is stable across re-harvests of the same record.
		public uint Sequence { get; set; }

		public CharacterStatId StatId { get; set; }

		// The record's effect-KIND tag, verbatim from the template (the ~53000-range word that opens the record).
		// 0 for a record that starts the body, where the tag would sit before it - unreadable, so never honored.
		public uint EffectKind { get; set; }

		// The magnitude, or - where the kind states a RANGE - its LOWER endpoint, `MaxAmount` carrying the upper one.
		public int Amount { get; set; }

		// The RANGE's upper endpoint, null on every kind that states a single number (#783). The `Hit` family states
		// `[Stat, min, max, …]` and the import kept only the min, so every damage and heal in the catalog shipped as
		// its own floor: `Hit` 53002/`SpecialHit` 53196 dropped key 37 and `InterpolatedHit` 53219 dropped key 170.
		// A roll between the two is the caller's business; what this table owes it is both ends.
		public int? MaxAmount { get; set; }

		// The stat the magnitude is a PERCENTAGE OF, non-null only on kind 53189 `AddPercentSkillModifier` (#807).
		// That kind is the one whose `Amount` is not a quantity in `StatId`'s own units: the record means "raise
		// `StatId` by `Amount`% of `SourceStatId`", and the client's tooltip branch picks between a same-stat and a
		// different-stat sentence by comparing the two.
		//
		// ⚠️ Its percentage is PLAIN (2..20), not the ×100 that kind 53067 stores - so the two cannot share a scale
		// constant, and `BuffStatEffects` converts on the way in rather than storing a rescaled value here. The row
		// stays the template's own number, like every other row in this table.
		//
		// ⚠️ NULL is the overwhelming case and means "the amount is in StatId's units", NOT "source == target". A
		// same-stat 53189 row carries `SourceStatId == StatId` explicitly, because the two readings differ for every
		// other kind and a null would make 53189's degenerate case indistinguishable from a flat `Modify`.
		public CharacterStatId? SourceStatId { get; set; }

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
		//
		// ⚠️ `InterpolatedHit` (53219) ramps too and used to leave all three NULL, because the ramp reader tested for
		// the interpolated MODIFY family's slots (174/175) rather than this family's (168/169 and 170/171 over the
		// same 172/173 levels) - so 1,779 rows shipped a level-1 number as if it were level-independent (#783).
		public int? HighAmount { get; set; }

		// The RANGE's upper endpoint at HighLevel - `MaxAmount` is to this what `Amount` is to `HighAmount`. Non-null
		// only on the interpolated HIT family, whose record is a PAIR of ramps: `Gangrenous Stench`'s tick states
		// 58.40-71.40 at level 75 ramping to 62.20-76.10 at 80, which is four magnitudes and two levels, not one
		// number (#783).
		public int? MaxHighAmount { get; set; }

		public uint? LowLevel { get; set; }

		public uint? HighLevel { get; set; }

		/// <summary>
		///     Everything this row SAYS, with the <see cref="Sequence" /> that says which element said it left out - so
		///     two rows carrying the same statement compare equal.
		/// </summary>
		/// <remarks>
		///     ⚠️ A record really does state the same element twice, and an engine must apply it ONCE. `Hellfire
		///     Stream` (8600) ends with two more `InterpolatedHit` elements at body offsets 1106 and 1194 that repeat,
		///     value for value, the level 1-5 and 6-10 bands it already stated at 390 and 478. They are not a scan
		///     artifact - the elements do not overlap, and two coincidental anchors reproducing eight matching values
		///     is not a thing that happens - so the repeat is in the shipped bytes. 1,298 rows over 361 spells are
		///     such a repeat.
		///     <para>
		///         WHY a record repeats is not decoded (the record also has 330 bytes this reader accounts for
		///         nothing of), which is exactly why the row is stored verbatim and the collapse happens at the point
		///         of USE: the table stays a faithful reading of the template, and no engine doubles a spell because
		///         of a structure nobody has read yet. Tracked as its own question rather than guessed at here.
		///     </para>
		/// </remarks>
		[NotMapped]
		public (uint Spell, CharacterStatId Stat, uint Kind, uint Period, int Amount, int? Max, int? High, int? MaxHigh, uint? LowLevel, uint? HighLevel) Statement =>
			(SpellId, StatId, EffectKind, PeriodCentiseconds, Amount, MaxAmount, HighAmount, MaxHighAmount, LowLevel, HighLevel);
	}
}
