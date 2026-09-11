using System.ComponentModel.DataAnnotations.Schema;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One stat modifier a feat applies. A feat's RDB record ends in a count-framed list of these,
	// so this is what the feat does - the part a server needs in order to apply it.
	//
	// Tail grammar, after the name:
	//   [u32 2][u32 14][u32 ListFrame(n)] then n GameAction elements:
	//     [u32 kind][u32 field][u32 4][u32 subCount][u32 1][u32 0][u32 2][u32 9][one u32 per key of the kind's key list]
	// The element is not a fixed 40 bytes and the eight leading words are not a descriptor: `kind` is a GameAction
	// type id and the payload is that type's key list, so an element is `8 + len(keyList)` words and a slot is
	// addressed by key, never by position (see GameActionTypes and docs/re/gameactionlist-grammar.md). The two
	// columns below are the (Stat, magnitude) pair, read by key.
	//
	// Only the elements that modify the carrier's own stats are here. The feat catalog carries 29 kinds and
	// most are something else entirely - a spell grant, a combo reference, an ability display, a proc - so
	// `ItemTemplateStatModifiers.ParseStatModifiers` gates on the type's key list: it must have a `Stat` slot (key 0)
	// and a magnitude (key 39 `Value`, or the interpolated family's key 174), and must not have a `Spell` (88) or
	// `Line` (1) slot, which would make it a modifier of that spell rather than of the character. The spell- and
	// line-scoped ones have their own tables (FeatSpellModifier / FeatSpellStatModifier / FeatSpellGrant);
	// the rest are undecoded, and `--report feat-effect-kinds` is the census that says which is which. Four kinds
	// survive today: 53045 `Modify`, 53220 `InterpolatedModify`, 53139 `SetFlag`, 53067 `ModifyAsRating`.
	//
	// What is not here, and is not in the RDB at all: Tier, prerequisites and cost. Diffing records across four
	// different trees moves only the id, the block count and the name - no header word tracks tree depth. That is
	// consistent with the client's own `"**Error: GDB and RDB data for feat "`: it joins server-side GDB data with
	// the RDB record, and the tree's shape lives on the GDB side.
	[PrimaryKey(nameof(FeatId), nameof(Sequence))]
	public class FeatEffect
	{
		public uint FeatId { get; set; }

		[ForeignKey(nameof(FeatId))]
		public virtual Feat Feat { get; set; } = null!;

		// Wire position within the feat's effect list, so the list regenerates in the order the client stores it.
		public int Sequence { get; set; }

		// The stat this modifies - the same id space as ItemStatModifier.StatId (0x324 Strength, 0x001 HealthMax and
		// the rest all appear on both).
		//
		// No [ConstrainToEnumValues], unlike ItemStatModifier. That attribute emits a check against the enum's
		// declared members, and CharacterStatId is a catalog of the stats we have named, not of the stat space: items
		// touch 17 ids and every one is declared, while feats touch 158 and 127 are not. The check would reject four
		// fifths of these rows on insert. Typed as the enum anyway, because the value is one - the names are just
		// incomplete, and a caller still wants GetScale() and a CharacterStatId-keyed dictionary without casting.
		public CharacterStatId StatId { get; set; }

		// The effect-kind tag that opens the record, and what says how to read Value:
		//   53045  `Modify`             a flat delta - the bulk of the table
		//   53220  `InterpolatedModify` a flat delta too, but Value is the low end of a level ramp (see below)
		//   53139  `SetFlag`            a flag write, not a bonus - every class feat carries (864 Player Flags, 5)
		//   53067  `ModifyAsRating`     a percent stored x100, so reading it flat turns "+2% Critical" into "+200"
		// Reading it is not optional: MaxStatAggregator sums only the flat ones. Carried rather than collapsed, for
		// the same reason SpellStatEffect carries it.
		public uint EffectKind { get; set; }

		// The modifier magnitude, as the raw wire value - the same convention as ItemStatModifier.Amount, and it must
		// be divided by CharacterStatId.GetScale() before it means anything.
		//
		// On the interpolated kinds this is only the ramp's low endpoint - see HighValue below.
		//
		// Raw wire units, not display units: reading these as display over-grants HealthMax by 100x and the attributes
		// by 10x. A subset invites the wrong reading ("Strength carries 1..9, HealthMax 100..700"); over the whole
		// table HealthMax runs to 99,000 and Strength to 2,300. Three things settle it:
		//   1. The same bytes are read twice. This column and ClassAttributeBase.Amount are both
		//      `ItemTemplateStatModifiers.Modifier.WireAmount`, off the same `<Class> Class Feat NNN` templates -
		//      `Necromancer Class Feat 071` carries HealthMax 38,600 and the ClassAttributeBases row for
		//      (Necromancer, 80) is 38,600 - and CharacterMaxStats.Pool adds that in hundredths and divides by 100
		//      for the +386 health verified against every captured pool.
		//   2. `Barbarian: HP (1) 10` carries 26,800 and `Vitality`'s top rank ramps to 80,000, against a level-80
		//      pool of ~6,300. No display reading survives that.
		//   3. The one unexplained flat health term in the model is 73 - tens, not thousands.
		//
		// Spells are the odd one out, not this: BuffStatEffects.SpellScale keeps attributes at scale 1 because the
		// spell record really does store those in display units. Three sources, three conventions; the parser a
		// source goes through is what says which one it uses.
		//
		// Signed, and it has to be: 44 shipped rows are negative - `Pet Mastery`'s Pet Damage Coefficient ladder
		// (-550 per rank down to -4400), the caster classes' Weapon Damage Ranged (-3000), `Mind Phase` and
		// `Subtlety`'s -1..-5. MaxStatAggregator sums every one of them, so an unsigned column turns a feat that
		// lowers a stat into one that raises it by four billion. The word on the wire is signed.
		public int Value { get; set; }

		// The level ramp the interpolated kinds carry (53220/53221/53068), null on every other kind. Such
		// an element is not a flat bonus at all: it grants `lerp(Value@LowLevel, HighValue@HighLevel)` over the
		// carrier's level, clamped at both ends - `Battle Roar` is +887 health at level 20 and +17500 at 80. Applying
		// Value alone under-grants by 20x at the ceiling.
		//
		// Three columns rather than an owned type because the ramp is a property of the row, and a nullable owned
		// entity would put the same three columns behind a navigation nothing else needs. Read them through
		// LevelRamp - the interpolation is the client's float arithmetic and belongs in one place.
		//
		// Signed, unlike Value. The feat corpus happens to hold no negative endpoint, but the spell corpus does
		// (SpellStatEffect carries -169), and both come out of the same parser; a uint column would turn that into
		// 4.29 billion the day a feat grows one.
		public int? HighValue { get; set; }

		// The two levels the endpoints are reached at. Only ever 1..100 in the shipped data.
		public uint? LowLevel { get; set; }

		public uint? HighLevel { get; set; }
	}
}
