namespace Faolan.Core.Enums
{
	// WHEN a SpellEffect row fires. The row's SpellId means something different per trigger - for OnCast it is the
	// spell the player casts, for OnFatality it is the buff that must be LIVE on the killer - so the trigger is part
	// of the key rather than a flag on the effect.
	public enum SpellEffectTrigger : byte
	{
		// The spell is cast (BehaviorControlCenter ActionType.CastSpell), and the cast resolves after its cast time.
		// The effect replaces the generic "apply the buff to the target" resolution.
		OnCast = 0,

		// A fatality lands while the row's SpellId is an active buff on the killer (#140). Retail states the shape in
		// the client's own words - "When you or your group perform a fatality and this buff is active, the Frozen
		// Hatred pet appears…" (spell 4377209's description, LDB category 50211).
		OnFatality = 1
	}
}
