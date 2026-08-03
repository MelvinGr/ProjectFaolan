namespace Faolan.GameServer.Combat
{
	// How a catalog combo is triggered - the marker that prefixes `Combo.Actions` (#455). A combo either has a key
	// SEQUENCE (Directional, the bulk of the catalog) or carries one of these markers instead, in which case the
	// single direction that follows the marker is a SELECTOR naming which of the family's five procs the row is.
	public enum ComboTrigger
	{
		// No marker: `Actions` is a key sequence the player performs (`UR,UP`).
		Directional,

		// The bare `action` marker - 69 catalog rows, and the only marker with NO direction after it. A placeholder
		// for a combo whose input is not modelled in the catalog at all (bow attacks, emotes, PowerAttack).
		Placeholder,

		// The caster set: a spellweave magic combo. Its five procs are its own (Exhaustion / Boiling Mana /
		// Mind Purge / Mana Leech / Discourage).
		MagicCombo,

		// The three reactive families - what has to happen for the proc to fire. All three share ONE effect per
		// direction (Debilitating Cleave / Rage / Furious Blow / Clumsy / Weaken), so the family is the trigger and
		// the direction is the effect.
		MagicReactive,
		MeleeReactive,
		ComboReactive
	}
}
