namespace Faolan.Core.Progression
{
	/// <summary>
	///     What a character's trained feats do to ONE combo (#697), summed across every feat that names it.
	/// </summary>
	/// <remarks>
	///     <para>
	///         Three feat tables feed this, and they do not all mean the same thing, which is why the record keeps
	///         them apart rather than adding them into one number:
	///     </para>
	///     <list type="bullet">
	///         <item>
	///             <c>FeatComboAttackModifier</c> (GameAction 53250 <c>ChangeInheritedWeaponAttack</c>) modifies one
	///             WEAPON ATTACK of the combo - its opening swing or its finishing one - so it lands on that step's
	///             swing damage and on its critical roll.
	///         </item>
	///         <item>
	///             <c>FeatComboModifier</c> (53209 <c>UpgradeCombo</c>) modifies the combo as a WHOLE ("improves the
	///             damage inflicted by the Blood for Aid combos"), and our model puts a combo's damage in its
	///             finisher SPELL, so that is where it lands.
	///         </item>
	///     </list>
	///     <para>
	///         Every percentage here is in HUNDREDTHS of a percent, the unit <c>SpellBoosts</c> already uses.
	///         53250's own columns are whole percents and are scaled on the way in - see the scale warning on
	///         <c>FeatComboAttackModifier.DpsPercent</c>.
	///     </para>
	/// </remarks>
	/// <param name="OpeningDamagePercentHundredths">Added to the combo's step-0 swing damage.</param>
	/// <param name="OpeningCriticalRating">Added to the attacker's Critical Rating for that swing only.</param>
	/// <param name="FinishingDamagePercentHundredths">Added to the last step's swing damage.</param>
	/// <param name="FinishingCriticalRating">Added to the attacker's Critical Rating for that swing only.</param>
	/// <param name="SpellDamagePercentHundredths">Added to the finisher SPELL's damage - the whole-combo axis.</param>
	public sealed record ComboFeatBonus(
		int OpeningDamagePercentHundredths,
		int OpeningCriticalRating,
		int FinishingDamagePercentHundredths,
		int FinishingCriticalRating,
		int SpellDamagePercentHundredths)
	{
		public static ComboFeatBonus None { get; } = new ComboFeatBonus(0, 0, 0, 0, 0);

		/// <summary>The swing-damage bonus for one step of the combo, in hundredths of a percent.</summary>
		public int SwingDamagePercentHundredths(bool isOpener, bool isFinisher)
		{
			return (isOpener ? OpeningDamagePercentHundredths : 0) + (isFinisher ? FinishingDamagePercentHundredths : 0);
		}

		/// <summary>The extra Critical Rating for one step of the combo.</summary>
		public int CriticalRating(bool isOpener, bool isFinisher)
		{
			return (isOpener ? OpeningCriticalRating : 0) + (isFinisher ? FinishingCriticalRating : 0);
		}
	}
}
