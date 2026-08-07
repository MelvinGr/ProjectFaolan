namespace Faolan.Core.Enums
{
	/// <summary>
	///     Which weapon attack of a combo a <c>ChangeInheritedWeaponAttack</c> modifier applies to (GameAction kind
	///     53250, key 84 - Gitea #593).
	/// </summary>
	/// <remarks>
	///     Both members are the CLIENT's own names, from the tooltip branch at <c>014a1dc5</c> in
	///     <c>FUN_0149e450</c>: it reads the slot and picks the localised <c>FinishingComboAttack</c> or
	///     <c>OpeningComboAttack</c> heading. The set is CLOSED rather than open-ended, because the branch RETURNS on
	///     any other value (<c>CMP EDI,1; JNZ &lt;bail&gt;</c>) - a third value would render nothing at all. Both
	///     appear in the shipped catalog (67 finishing, 248 opening) and the feat names corroborate the polarity:
	///     `Fierce Opening` and `Dual Master`, whose text is "increases the OPENING damage done by all combos", carry
	///     <see cref="Opening" />.
	///     <para>
	///         The descriptor label for key 84 is <c>Level</c>, which is what it means on the types that carry it as
	///         a real level. It does not mean that here - see the per-type key warning in <c>GameActionTypes</c>.
	///     </para>
	/// </remarks>
	public enum ComboAttackSlot : uint
	{
		/// <summary>The combo's FINISHING attack - the last step, where a combo's damage lands.</summary>
		Finishing = 0,

		/// <summary>The combo's OPENING attack - the starter swing that begins the combo.</summary>
		Opening = 1
	}
}
