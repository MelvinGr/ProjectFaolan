using System.ComponentModel.DataAnnotations.Schema;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One stat modifier a feat applies (Gitea #251 step 2). A feat's RDB record ends in a count-framed list of these,
	// so this is what the feat DOES - the part a server needs in order to apply it.
	//
	// Tail grammar, after the name:
	//   [u32 2][u32 14][u32 ListFrame(n)] then n GameAction ELEMENTS:
	//     [u32 kind][u32 field][u32 4][u32 subCount][u32 1][u32 0][u32 2][u32 9][one u32 per key of the kind's key list]
	// The element is not a fixed 40 bytes and the eight leading words are not a descriptor: `kind` is a GameAction
	// TYPE id and the payload is that type's KEY LIST, so an element is `8 + len(keyList)` words and a slot is
	// addressed by key, never by position (#591 - see GameActionTypes and docs/re/gameactionlist-grammar.md). The two
	// columns below are the (Stat, magnitude) pair, read BY KEY.
	//
	// ⚠️ Only the elements that modify the CARRIER's own stats are here (#597). The feat catalog carries 29 kinds and
	// most are something else entirely - a spell grant, a combo reference, an ability display, a proc - so
	// `ItemTemplateStatModifiers.ParseStatModifiers` gates on the type's key list: it must have a `Stat` slot (key 0)
	// and a magnitude (key 39 `Value`, or the interpolated family's key 174), and must NOT have a `Spell` (88) or
	// `Line` (1) slot, which would make it a modifier of that spell rather than of the character. The spell- and
	// line-scoped ones have their own tables (FeatSpellModifier / FeatSpellStatModifier / FeatSpellGrant, #590/#593);
	// the rest are undecoded, and `--report feat-effect-kinds` is the census that says which is which. Four kinds
	// survive today: 53045 `Modify`, 53220 `InterpolatedModify`, 53139 `SetFlag`, 53067 `ModifyAsRating`.
	//
	// ⚠️ What is NOT here, and is not in the RDB at all: TIER, PREREQUISITES and COST. Diffing records across four
	// different trees moves only the id, the block count and the name - no header word tracks tree depth. That is
	// consistent with the client's own `"**Error: GDB and RDB data for feat "`: it JOINS server-side GDB data with
	// the RDB record, and the tree's shape lives on the GDB side. See #251.
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
		// ⚠️ NO [ConstrainToEnumValues], unlike ItemStatModifier. That attribute emits a CHECK against the enum's
		// DECLARED members, and CharacterStatId is a catalog of the stats we have NAMED, not of the stat space: items
		// touch 17 ids and every one is declared, while feats touch 158 and 127 are not. The CHECK would reject four
		// fifths of these rows on insert. Typed as the enum anyway, because the value IS one - the names are just
		// incomplete, and a caller still wants GetScale() and a CharacterStatId-keyed dictionary without casting.
		public CharacterStatId StatId { get; set; }

		// The effect-KIND tag that opens the record, and what says how to read Value:
		//   53045  `Modify`             a flat delta - the bulk of the table
		//   53220  `InterpolatedModify` a flat delta too, but Value is the LOW end of a LEVEL RAMP (see below)
		//   53139  `SetFlag`            a flag WRITE, not a bonus - every class feat carries (864 Player Flags, 5)
		//   53067  `ModifyAsRating`     a PERCENT stored x100, so reading it flat turns "+2% Critical" into "+200"
		// Reading it is not optional: MaxStatAggregator sums only the flat ones. Carried rather than collapsed, for
		// the same reason SpellStatEffect carries it.
		public uint EffectKind { get; set; }

		// The modifier magnitude, in DISPLAY units.
		//
		// ⚠️ On the INTERPOLATED kinds this is the ramp's LOW endpoint, not the whole bonus. That element is
		// `lerp(low@lowLevel, high@highLevel)` over the carrier's Casting/Apprentice Level - `Battle Roar` is +887 at
		// level 20 and +17500 at 80 - and only the low endpoint fits this column, so a high-level character is
		// under-granted. The other three slots and the ramp are Gitea #599.
		//
		// ⚠️ NOT the same convention as ItemStatModifier.Amount, which is the RAW WIRE value and must be divided by
		// CharacterStatId.GetScale(). These are already divided: Strength (scale 10) carries 1..9 here, which as raw
		// wire would be +0.1 Strength, and HealthMax (scale 100) carries 100..700, which as raw wire would be +1 HP.
		// Applying the gear convention to these would silently under-grant every feat by 10x or 100x.
		public uint Value { get; set; }
	}
}
