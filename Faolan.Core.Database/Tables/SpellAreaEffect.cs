using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One AREA application a spell performs - "apply spell X to up to N entities within R metres", optionally on a
	// PERIOD. Decoded from the spell record's area-targeting GameAction elements (`GameActionTypes.AreaTargetKinds`:
	// AreaFormula, FilteredAreaFormula, ConeFormula, …), which were invisible to the element scan until #772 because
	// they carry SUB-RECORDS.
	//
	// ⚠️ A spell has SEVERAL of these, which is why it is a table and not columns on `Spell`. `Gangrenous Stench`
	// (221070) carries two - one instant, one every 4 s - and `Flesh to Worms` five. Collapsing them onto the parent
	// would have to pick one and silently drop the rest, and the one it dropped would be the aura.
	//
	// The two SHAPES, and the reason both are needed (the maintainer, 2026-08-09):
	//   * `PeriodCentiseconds == 0` - the cast applies the effect to everyone in radius ONCE. `Life Leech` (220979)
	//     applies 220980 in 5 m to 12 targets, and that is the whole of it.
	//   * `PeriodCentiseconds > 0` - an AURA. The cast puts a buff on the CASTER, and that buff re-applies the effect
	//     to everyone in radius every period, for as long as it is up. `Gangrenous Stench` is a 25 s self-buff whose
	//     aura hits a 7 m radius every 4 s.
	// Both read identically off the wire; only 0x2d6 (the period) tells them apart, which is exactly the word the old
	// element scan treated as a signature byte and discarded.
	//
	// Every value here is in the in-client tooltip: 221070 renders "7.00m radius up to 12 targets" and "every
	// 4 seconds" from `RadiusMetres` 7, `MaxTargets` 12 and `PeriodCentiseconds` 400.
	//
	// KEY is the natural (SpellId, Sequence) - Sequence being the element's position in the record, so a re-harvest
	// of the same record reproduces the same ids rather than churning a surrogate (see the model conventions in
	// CLAUDE.md). `EffectSpellId` is deliberately NOT a foreign key, matching `Spell.EffectSpell`: it names a record
	// in the client's catalog, and a spell whose effect we have not catalogued must still keep its area definition
	// rather than fail the import.
	[PrimaryKey(nameof(SpellId), nameof(Sequence))]
	public class SpellAreaEffect
	{
		public uint SpellId { get; set; }

		[ForeignKey(nameof(SpellId))]
		public virtual Spell Spell { get; set; } = null!;

		// The element's ordinal within the record's element list - stable across re-harvests of the same record.
		public uint Sequence { get; set; }

		// The GameAction type that stated this area (AreaFormula 53087, FilteredAreaFormula 53088, ConeFormula 53259,
		// …). Carried rather than flattened away because the KIND is what says how targets are chosen - a cone is not
		// a ring - and the server honours only the shapes it can justify.
		public uint Kind { get; set; }

		// The spell applied to each entity in the area (the element's `KeySpell`). This is the EFFECT record, never
		// the castable: 221070 applies 221069, 220979 applies 220980.
		public uint EffectSpellId { get; set; }

		// Radius in METRES, straight from the element's `KeyRadius` - the client renders it as `%.2fm`, so 7 is the
		// "7.00m" of the tooltip.
		public uint RadiusMetres { get; set; }

		// The cap the tooltip renders as "up to N targets" (the element's key 170). 0 = the element stated none.
		public uint MaxTargets { get; set; }

		// The AURA period in CENTISECONDS - the element's 0x2d6. 0 means this area is applied ONCE, at cast.
		public uint PeriodCentiseconds { get; set; }
	}
}
