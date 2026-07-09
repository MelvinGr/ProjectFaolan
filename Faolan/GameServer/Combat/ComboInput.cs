namespace Faolan.GameServer.Combat
{
	// The canonical combat-input directions a combo step requires. AoC combos are a SEQUENCE of directional melee
	// swings the player performs in order (Combo.Actions, e.g. "UR,UP"); completing the sequence fires the finisher.
	// The five directional keys (the four quadrants + overhead) dominate the catalog; Head/Thrust are rare specials.
	// (The on-wire direction code carried by each NormalAttack maps INTO this enum - that mapping is pinned from a
	// capture separately; this enum is the wire-independent vocabulary the Combo.Actions field parses into.)
	public enum ComboInput
	{
		UpperLeft,
		UpperRight,
		LowerLeft,
		LowerRight,
		Up, // overhead
		Head,
		Thrust
	}
}
