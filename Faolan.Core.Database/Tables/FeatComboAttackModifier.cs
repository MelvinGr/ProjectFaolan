using System.ComponentModel.DataAnnotations.Schema;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	/// <summary>
	///     One WEAPON ATTACK of one combo that a feat modifies (#593) - "Fierce Opening: increases the opening damage
	///     done by all combos", "Brutal Maiming: increases the chance of the Disable combo inflicting critical
	///     damage", "Improved Shield Mastery: augments the combos given by Shield Mastery".
	/// </summary>
	/// <remarks>
	///     <para>
	///         The GameAction kind is 53250 <c>ChangeInheritedWeaponAttack</c>, <c>[187 combo, 84, 66, 67, 68, 69]</c>
	///         - 315 records over 12 feats. It is the SIBLING of <see cref="FeatComboModifier" /> (53209
	///         <c>UpgradeCombo</c>) and not a variant of it: that one moves the combo as a whole (its damage, its
	///         reuse time), this one moves one of its two ATTACKS, and along axes the other has no slot for at all.
	///     </para>
	///     <para>
	///         Every column is named from the client's own tooltip branch, which renders one localised string per
	///         non-zero slot - <c>IncreaseComboDPS</c>, <c>IncreaseComboCriticalChance</c>,
	///         <c>IncreaseComboHateAdjustment</c>, <c>ChangeComboDamageType</c>, each with a <c>Decrease…</c> twin
	///         picked on the value's sign, under a <c>FinishingComboAttack</c> / <c>OpeningComboAttack</c> heading.
	///         See <c>docs/re/gameactionlist-grammar.md</c>.
	///     </para>
	///     <para>
	///         ⚠️ Exactly ONE magnitude is non-zero on all 315 shipped records, so the three are alternatives in
	///         practice rather than a vector. They are kept as three plain signed ints anyway, because 0 is what the
	///         client's own <c>TEST/JZ</c> means - "this axis is untouched" - and nothing says a record may not move
	///         two.
	///     </para>
	/// </remarks>
	[PrimaryKey(nameof(FeatId), nameof(Sequence))]
	public class FeatComboAttackModifier
	{
		public uint FeatId { get; set; }

		[ForeignKey(nameof(FeatId))]
		public virtual Feat Feat { get; set; } = null!;

		/// <summary>Wire position within the feat's modifier list, so the list regenerates in the client's order.</summary>
		public int Sequence { get; set; }

		/// <summary>
		///     The combo whose attack this modifies - <see cref="Combo.Id" />. Deliberately NOT a foreign key, for the
		///     same reason <see cref="FeatComboModifier.ComboId" /> is not: 110 of the 112 distinct references resolve
		///     against the shipped combo catalog and 2 do not, and an unresolved reference is a gap in <c>Combos</c>
		///     (Gitea #581) rather than a reason to drop the feat's row.
		/// </summary>
		public uint ComboId { get; set; }

		/// <summary>Which of the combo's two weapon attacks the modifier applies to - the client's own two names.</summary>
		public ComboAttackSlot AttackSlot { get; set; }

		/// <summary>
		///     The attack's DAMAGE delta as a WHOLE percent: 30 = +30%. Signed - the tooltip picks <c>Increase</c> vs
		///     <c>Decrease</c> on the sign - and 0 when the record does not touch damage.
		///     <para>
		///         ⚠️ NOT the same scale as <see cref="FeatComboModifier.DamagePercentHundredths" />, even though both
		///         come out of GameActionStat slot <c>0x315</c>: the 53209 branch renders it <c>× 0.01</c> through the
		///         float formatter and this one pushes the raw int at the integer formatter with a hardcoded <c>%</c>.
		///         Same slot, different arithmetic, one type apart.
		///     </para>
		/// </summary>
		public int DpsPercent { get; set; }

		/// <summary>
		///     The attack's CRITICAL-CHANCE delta as a RATING: the client renders it <c>× 0.025</c> with a <c>%</c>
		///     suffix, so 40 points make one percentage point and `Brutal Blows`' 400 is +10%. Signed; 0 when
		///     untouched. Stored raw, as the record carries it.
		/// </summary>
		public int CriticalChanceRating { get; set; }

		/// <summary>
		///     The attack's HATE delta as a WHOLE percent, same signedness and same raw-int rendering as
		///     <see cref="DpsPercent" />. Two rows in the whole catalog carry it, both on the Guardian's `Improved
		///     Shield Mastery` (+2 and +5), which is exactly the feat a threat modifier belongs to.
		/// </summary>
		public int HatePercent { get; set; }

		/// <summary>
		///     The damage TYPE the attack is changed to - an id the client resolves through
		///     <c>GetLocalisedString(600, value)</c>, i.e. a name-table lookup rather than a magnitude. Zero on every
		///     shipped record, so nothing has ever exercised it; carried because the slot exists and a non-zero one
		///     must not be silently read as a number.
		/// </summary>
		public uint DamageTypeId { get; set; }

		/// <summary>
		///     The effect-KIND tag that opens the record. Only 53250 is imported here; carried for the same reason
		///     every other feat table carries it - so a second kind landing in this shape is visible rather than
		///     silently merged into the first.
		/// </summary>
		public uint EffectKind { get; set; }
	}
}
