namespace Faolan.Core.Enums
{
	/// <summary>
	///     When a <c>SpellEffect</c> row fires.
	/// </summary>
	/// <remarks>
	///     The row's <c>SpellId</c> means something different per trigger - for <see cref="OnCast" /> it is the spell
	///     the player casts, for <see cref="OnFatality" /> it is the buff that must be live on the killer - so the
	///     trigger is part of the key rather than a flag on the effect.
	/// </remarks>
	public enum SpellEffectTrigger : byte
	{
		/// <summary>
		///     The spell is cast (BehaviorControlCenter <c>BehaviorId.MagicAttack</c>) and the cast resolves after its
		///     cast time. The effect replaces the generic "apply the buff to the target" resolution.
		/// </summary>
		OnCast = 0,

		/// <summary>
		///     A fatality lands while the row's <c>SpellId</c> is an active buff on the killer. Retail states the shape
		///     in the client's own words - "When you or your group perform a fatality and this buff is active, the
		///     Frozen Hatred pet appears..." (spell 4377209's description, LDB category 50211).
		/// </summary>
		OnFatality = 1
	}
}
