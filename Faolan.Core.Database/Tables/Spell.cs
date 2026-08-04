using System.ComponentModel.DataAnnotations;

namespace Faolan.Core.Database.Tables
{
	//[Index(nameof(Name), IsUnique = true)]
	public class Spell : BaseEntity
	{
		public byte Level { get; set; }

		// RDB field 123 (0x7B) of the spell's type-1040005 record, carried RAW.
		//
		// ⚠️ It is the rank for MOST spells but NOT all, so do not read it as one without checking. Measured against
		// the "(Rank N)" suffix in the name over the 883 spells that have both: 669 agree, and the other 214 are a
		// clean rank x10 - "Healing Benediction (Rank 1/2/3)" carries 10/20/30 where "Wave of Life (Rank 2)" carries
		// 2. The scale differs by spell FAMILY and nothing found so far discriminates the two: field 128 is constant
		// (2) across that family's ranks, so it is not the selector either.
		//
		// Kept raw rather than normalised because the two cases are genuinely ambiguous at 10 - a true rank 10 and a
		// x10-encoded rank 1 are the same value - and guessing would silently corrupt one of them. The #367 field dump
		// labelled 123 "rank"; this is that label qualified, not overturned.
		public byte Rank { get; set; }

		/// <summary>
		///     RDB field 128 (0x80) of the spell's type-1040005 record, carried RAW. When it is
		///     <c>ConanClass × 1000 + lineIndex</c> it is the spell's LINE - what a feat's spell modifier points at
		///     (<see cref="FeatSpellModifier.SpellLineId" />): 41000 is Necromancer line 0, and the five
		///     <c>Flesh to Worms</c> ranks all carry it, which is how a feat boosts a spell without naming a rank.
		///     0 when the record has no such field (only 3,158 of 21,502 templates carry one).
		/// </summary>
		/// <remarks>
		///     ⚠️ <c>ConanClass × 1000 + index</c> is the common CASE, not the encoding (#591). Line <c>50000</c> is a
		///     real, working line whose high part is not a class - 50 (0x32) is outside the class registry entirely -
		///     and it resolves perfectly: its five spells are the five ranks of <c>Inferno of Amher</c>, and the five
		///     feat modifiers pointing at it are the five ranks of <c>Blessing of Amher</c>, a clean
		///     2.5/5/7.5/10/15% ladder. The feat is named for the spell it boosts. So an id whose <c>/1000</c> is not
		///     a class is not a corrupt row and not a reason to filter - it is a line the class reading does not
		///     explain. (<c>440061</c>, one spell, is the other such id.)
		///     <para>
		///     The field is also OVERLOADED and only its class-encoded values are line ids. It also holds small numbers
		///     (1, 2, 4, …) shared by 25-30 unrelated spells apiece - far too coarse to be a line, which is 3-7 ranks -
		///     so those are some other grouping and are not decoded. Carried raw rather than filtered because the join
		///     cannot collide: a feat only ever references a class-encoded value.
		///     <para>
		///         This also qualifies the note on <see cref="Rank" /> above, which said field 128 "is constant (2)
		///         across that family's ranks". It is constant across a family - that part was right - but the 2 was
		///         one family's value, not the field's meaning.
		///     </para>
		///     </para>
		/// </remarks>
		public uint LineId { get; set; }

		/// <summary>
		///     RDB field 847 (0x34F) of the spell's type-1040005 record: the ATTRIBUTE this spell's magnitude scales
		///     off, as a wire stat id. Exactly three values over the 716 records that carry one -
		///     <see cref="CharacterStatId.Intelligence" /> (0x328, ×482), <see cref="CharacterStatId.Wisdom" />
		///     (0x32A, ×224) and <see cref="CharacterStatId.Strength" /> (0x324, ×10) - and it splits where a reader
		///     would expect: the Demonologist's nukes (Hellfire Stream, Shockstrike, Fires of Gehenna) on
		///     Intelligence, the curses and DoTs (Venomous Curse, Poisonous Incantation) on Wisdom. 0 when the record
		///     carries no such field, which is the overwhelming majority - see the remarks for what happens then.
		/// </summary>
		/// <remarks>
		///     ⚠️ This is a per-spell OVERRIDE, not the general rule, which is why a 0 here is not a gap. The client
		///     picks a caster's scaling attribute by CLASS, not by spell: its magic-damage accessor
		///     (<c>FUN_01118ce0</c>) defaults to Wisdom (0x32A) and switches to Intelligence (0x328) only for
		///     Necromancer (0x29), Herald of Xotli (0x2B) and Demonologist (0x2C) - see
		///     <c>docs/rdb/spell-scaling.md</c>. So the class default covers every spell and field 847 names the
		///     handful that deviate. <c>CombatResolver.ResolveSpellDamage</c> reads it that way round.
		/// </remarks>
		public uint ScalingAttribute { get; set; }

		public float DamageRating { get; set; } // spell damage rating percent (RDB field 0x15C / 100)
		public float Range { get; set; } // target range in metres (RDB field 0x11F / 100)

		// Damage-over-time per tick on HealthCurrent, extracted from captured StatDamageIIR (0xE9F24184) records keyed
		// by base spell id (the +41 signed delta; its affected stat at +33 is 0x1B HealthCurrent). Negative = a bleed
		// debuff (Rend -8, 0x342F23 -5); 0 = not a DoT. The world tick applies this to the target's vitals + sends the
		// StatDamageIIR while the debuff is active (#274). DotType is the +45 damage-school flag (1/4), carried so the
		// rebuilt packet matches retail.
		public int DotDelta { get; set; }

		[MaxLength(128)]
		public string Name { get; set; } = null!;

		// The tooltip description, from LDB category 50211 keyed by the SPELL id - the same text the client shows in a
		// spell/buff tooltip ("When you or your group perform a fatality and this buff is active, the Frozen Hatred pet
		// appears…"). NULL when the LDB has none for this spell (only ~3.6k of the 20k spells carry one), or when the
		// RDB-backed rdb-ldb-names module has not been run (it is opt-in, needing --RdbDir).
		//
		// This is the one source that states an effect in WORDS, which is why it is worth storing: the spell record
		// itself carries magnitudes but not their meaning, so the description is how an effect gets identified (it is
		// what pinned Frozen Hatred's fatality proc, #434) and how a decoded magnitude gets sanity-checked. Some carry
		// the client's inline gender markup (#name{m:…|f:…}) verbatim - stored as-is, it is the client's own text.
		[MaxLength(2048)]
		public string? Description { get; set; }

		public uint DotType { get; set; }

		// How often the DoT ticks, in milliseconds. MEASURED from the captures rather than lifted from the RDB (#437):
		// a StatDamageIIR carries its spell's base id at word 12, so the gap between consecutive ticks of one spell on
		// one target IS the interval. The gaps of a real DoT cluster tightly and the values are round - 500 ms to 11 s.
		//
		// Which is why this column exists at all: the tick used ONE constant, 2 s, for every DoT. That is right for the
		// Incinerate family and wrong everywhere else - Ripper Volley and Archfiend tick twice as fast as we ran them,
		// Ether Drain two and a half times slower, Hellfire Breath five times slower. 0 = unmeasured, and the tick
		// falls back to 2 s.
		//
		// Measured by the `dot-effects` importer module, NOT by hand: the first 38 came from a one-off scan that was
		// never shipped, so the number could not be improved without redoing the work. Now it is 53 of the 84 DoT
		// spells, and re-running the module is all it takes to widen that as the corpus grows. What decides whether
		// observed gaps establish an interval at all is DotIntervalEstimator, which rejects rather than guesses -
		// the remaining 31 have no interval because their ticks were never observed often enough, not because
		// nobody looked.
		public uint DotIntervalMs { get; set; }

		// Buff/effect duration in milliseconds. Imported from RDB field 0x008, which is in CENTISECONDS (×10ms - the
		// same unit as the cast-time field; calibrated in-client: Rend 700->7s, Butcher 400->4s, Exhausted 10000->100s),
		// so it is stored here ×10 as real ms. 0 = no self-duration on this record (an instant spell, or an ability
		// whose timed effect lives on a separate EffectSpell record). Drives server-side buff expiry: when a self-buff
		// with a known duration elapses, the world tick sends the retail end-twin removal (rank byte -> 1, empty
		// payload) so the icon disappears at 0 instead of sticking (#274).
		public uint DurationMs { get; set; }

		public uint EffectSpell { get; set; }

		// Backfilled from the RDB spell-data record (type 1040005) by RdbSpellImporter (#236):
		public ushort CastTime { get; set; } // milliseconds (RDB field 0x126 * 10)

		// Recast / cooldown in SECONDS. Backfilled from the ellatha combos list (the one bulk source that carries it)
		// by EllathaSpellLevelImporter for combo rows. Magic-spell cooldown is NOT in any bulk source (proven not in
		// the RDB record, and not on ellatha's spell list), so it stays 0 for spells - capture key ones by hand from
		// retail tooltips (Captures/spell_cooldowns_observed.tsv). Seconds (not ms like CastTime) to match the source
		// and fit values like Death's Terror 90s in a ushort. (#78)
		public ushort Cooldown { get; set; }

		/// <summary>
		///     RDB field 132 (0x084) of the spell's type-1040005 record - the client's
		///     <see cref="CharacterStatId.ManaCost" /> stat id - carried RAW, because the client reads it in TWO modes
		///     and the selector is not in this record. 0 = the spell is free, which is retail's own reading of an
		///     absent property. 1,691 of the 20,847 catalogued spells carry a cost.
		/// </summary>
		/// <remarks>
		///     Recovered from the client, not fitted (#629). <c>FUN_008d6f70</c> - the function behind the spell
		///     tooltip's <c>ManaCostToolTip</c> line - is:
		///     <code>
		///     cost = spell.GetIntStat(0x84);                               // this field, clamped to >= 0
		///     if (spellFlags &amp; 4)                                          // the percent-of-pool mode
		///         cost = (int)(caster.GetIntStat(0x1FB) * cost * 0.01f);   // 0x1FB = ManaMax, wire units
		///     cost = (int)((modifier.Amount * 0.0001f + 1.0f) * (modifier.Value + cost));
		///     </code>
		///     and the tooltip prints <c>cost * 0.01</c>, i.e. it divides the wire's hundredths back to display units.
		///     The two modifier terms are exactly the <see cref="FeatSpellStatModifier" /> pair (an absolute
		///     <c>Value</c> plus an <c>Amount</c> in hundredths of a percent) that feats already target at
		///     <see cref="CharacterStatId.ManaCost" />, which is what says this is the real cast-cost path and not a
		///     display-only accessor.
		///     <para>
		///         So with the flag set the value is a PERCENTAGE of the caster's mana pool (<c>Ice Strike</c> 9 = 9%),
		///         and without it an absolute cost in wire hundredths (<c>Dark Knowledge (Rank 4)</c> 42200 = 422
		///         mana). ⚠️ Which RDB field feeds that flag is NOT decoded - Gitea #630 - so
		///         <c>CombatResolver.ResolveSpellManaCost</c> picks the mode by MAGNITUDE instead. The data is
		///         sharply bimodal, which is what makes that safe for now: 1,209 rows sit at 1..100 and 452 at
		///         1000+, with 30 in between. Both modes carry real player spells and neither is a legacy artifact -
		///         one spell LINE can even mix them (<c>Dark Knowledge</c> rank 1 is 10%, ranks 2-4 are 128/265/422
		///         flat) - so the threshold is a stand-in for the flag, not a theory about it.
		///     </para>
		/// </remarks>
		public ushort ManaCost { get; set; }
	}
}
