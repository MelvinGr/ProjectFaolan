using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One effect link a spell states: another spell it runs, and how. `Kind` is the GameAction type that stated it and
	// is what separates the two forms - reading the link without it would say "221070 applies 221069" and lose whether
	// that is to everyone in a radius or to the one target:
	//   * an area kind (`GameActionTypes.AreaTargetKinds`: AreaFormula, FilteredAreaFormula, ConeFormula, …) - "apply
	//     spell X to up to N entities within R metres", optionally on a period. These carry sub-records, so an element
	//     scan that does not descend into them misses the link entirely.
	//   * `CastNano` (53051) - "also run spell X", with no area at all: `RadiusMetres` and `MaxTargets` are 0 and the
	//     link is a chain edge to the same subject. 11,077 elements across the catalog. Without these rows a spell's
	//     headline effect can be unreachable - `Gangrenous Stench` reaches 3963058 `Physical Wrack` (the -10%
	//     Slashing/Piercing/Crushing/Poison its tooltip shows) only through a chain edge; 26 spells reference 3963058.
	// So a consumer must branch on the kind rather than treating every row as an area - see `SpellAreaCatalog`,
	// whose area accessors filter to the area kinds and whose chain accessor takes the rest.
	//
	// A spell has several of these, which is why it is a table and not columns on `Spell`. `Gangrenous Stench`
	// (221070) carries two - one instant, one every 4 s - and `Flesh to Worms` five. Collapsing them onto the parent
	// would have to pick one and silently drop the rest, and the one it dropped would be the aura.
	//
	// The two shapes, and the reason both are needed (the maintainer, 2026-08-09):
	//   * `PeriodCentiseconds == 0` - the cast applies the effect to everyone in radius once. `Life Leech` (220979)
	//     applies 220980 in 5 m to 12 targets, and that is the whole of it.
	//   * `PeriodCentiseconds > 0` - an aura. The cast puts a buff on the caster, and that buff re-applies the effect
	//     to everyone in radius every period, for as long as it is up. `Gangrenous Stench` is a 25 s self-buff whose
	//     aura hits a 7 m radius every 4 s.
	// Both read identically off the wire; only 0x2d6 (the period) tells them apart, which is exactly the word the old
	// element scan treated as a signature byte and discarded.
	//
	// Every value here is in the in-client tooltip: 221070 renders "7.00m radius up to 12 targets" and "every
	// 4 seconds" from `RadiusMetres` 7, `MaxTargets` 12 and `PeriodCentiseconds` 400.
	//
	// Key is the natural (SpellId, Sequence) - Sequence being the element's position in the record, so a re-harvest
	// of the same record reproduces the same ids rather than churning a surrogate (see the model conventions in
	// CLAUDE.md). `EffectSpellId` is deliberately not a foreign key, matching `Spell.EffectSpell`: it names a record
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

		// `Kind` for a chain edge - `e_Type_GC_GameAction_CastNano`, "also run spell X" on the same subject, with no
		// area at all. Named here rather than repeated as a literal: this layer cannot reach
		// `GameActionTypes` (it lives in Faolan.Core.Rdb, which the database does not reference), and the value is
		// the one every chain query keys on.
		public const uint CastNanoKind = 53051;

		// The GameAction type that stated this link (AreaFormula 53087, FilteredAreaFormula 53088, ConeFormula 53259,
		// … or CastNano - see CastNanoKind). Carried rather than flattened away because the kind is what says how
		// targets are chosen - a cone is not a ring, and a chain edge is not an area at all - and the server honours
		// only the shapes it can justify.
		public uint Kind { get; set; }

		// The spell this link runs - the element's `KeySpell` on an area kind, its key 39 on `CastNano`. This is the
		// effect record, never the castable: 221070 applies 221069, 220979 applies 220980.
		public uint EffectSpellId { get; set; }

		// Radius in metres, straight from the element's `KeyRadius` - the client renders it as `%.2fm`, so 7 is the
		// "7.00m" of the tooltip. 0 on a `CastNano` link, which states no area.
		public uint RadiusMetres { get; set; }

		// The cap the tooltip renders as "up to N targets" (the element's key 170). 0 = the element stated none.
		public uint MaxTargets { get; set; }

		// The aura period in centiseconds - the element's 0x2d6. 0 means this area is applied once, at cast.
		//
		// The client names this word. `FUN_0089b390` logs "Fixing broken gameaction data, Duration was 0 while
		// interval was <this>. Setting Duration to 1" - so 0x2d6 is the interval and 0x2d5 is the duration, and the
		// two are read together.
		public uint PeriodCentiseconds { get; set; }

		/// <summary>
		///     How many times the element runs - the element's <c>0x2d5</c>, the word the client's own repair
		///     message calls the duration. <c>-1</c> is unbounded.
		/// </summary>
		/// <remarks>
		///     <para>
		///         It is a count, not a time: over the whole catalog it takes <c>1</c> (run once), <c>-1</c>
		///         (repeat while the carrier lasts) and a thin tail of small integers - 2, 3, 4, 5 … 40 - and
		///         `FUN_00869f90` schedules an element rather than running it inline when
		///         <c>interval &gt; 0 || duration &gt; 1</c>.
		///     </para>
		///     <para>
		///         It is also what settles what a period means on a chain edge, which the interval alone cannot:
		///         both readings are right, for different rows. Of the `CastNano` links that carry an interval, 265
		///         have <c>Repeats == -1</c> - a genuine cadence, re-run every N - and 245 have <c>Repeats == 1</c>,
		///         which runs once, so there the interval is a delay. Three more state a small count.
		///     </para>
		///     <para>
		///         Signed, and stored signed. Reading it as unsigned turns the unbounded case into 4.29 billion.
		///     </para>
		/// </remarks>
		public int Repeats { get; set; }
	}
}
