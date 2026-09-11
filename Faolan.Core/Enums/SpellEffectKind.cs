namespace Faolan.Core.Enums
{
	/// <summary>
	///     What a <c>SpellEffect</c> row does when its trigger fires. Each kind reads a different subset of the row's
	///     columns; the column comments on <c>SpellEffect</c> say which.
	/// </summary>
	public enum SpellEffectKind : byte
	{
		/// <summary>
		///     Spawn a creature type as a pet owned by the player (creature spawn, PetIIR owner-assign, follow). Reads
		///     <c>Template</c>, <c>Combat</c>, and - for a pet with a lifetime - <c>DurationMs</c>/<c>MaxDurationMs</c>.
		/// </summary>
		SummonPet = 0,

		/// <summary>
		///     Apply <c>TargetSpellId</c> to the player as a self-buff: the proc half of a triggered effect, as
		///     distinct from the buff the cast itself applies.
		/// </summary>
		ApplySpell = 1
	}
}
