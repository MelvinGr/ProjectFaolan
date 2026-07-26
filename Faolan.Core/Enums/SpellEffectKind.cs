namespace Faolan.Core.Enums
{
	// WHAT a SpellEffect row does when its trigger fires. Each kind reads a different subset of the row's columns;
	// the column comments on SpellEffect say which.
	public enum SpellEffectKind : byte
	{
		// Spawn a creature TYPE as a pet owned by the player (creature + PetIIR owner-assign + follow, #307). Reads
		// Template, Combat, and - for a pet with a lifetime - DurationMs/MaxDurationMs.
		SummonPet = 0,

		// Apply TargetSpellId to the player as a self-buff. The proc half of a triggered effect (the Frozen Hatred
		// fatality proc, #140/#434), as distinct from the buff the cast itself applies.
		ApplySpell = 1
	}
}
