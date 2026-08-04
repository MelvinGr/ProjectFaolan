namespace Faolan.GameServer.Combat
{
	// How a catalog combo is triggered - the marker that prefixes `Combo.Actions` (#455). A combo either has a key
	// SEQUENCE (Directional, the bulk of the catalog) or carries one of these markers instead, in which case the
	// single direction that follows the marker is a SELECTOR naming which of the family's five procs the row is.
	public enum ComboTrigger
	{
		// No marker: `Actions` is a key sequence the player performs (`UR,UP`).
		Directional,

		// The bare `action` marker - 69 catalog rows, and the only marker with NO direction after it. It is the NPC
		// combo set: all 69 rows are named `combo_npc_*` (`combo_npc_generic_polearm`,
		// `combo_npc_guardian_polearm_1_sweep`, ...) and the corpus confirms it from the other side - those ids
		// appear ONLY s2c, with an NPC's actor handle, never in a c2s request. So there is no key sequence because
		// there is no keyboard: the AI drives them.
		// [V3: ComboWireTests.NpcComboRows_AreTheActionMarkerSet + the corpus census in docs/subsystems/combos.md]
		// ⚠️ This is NOT the "combos the catalog does not model" bucket that was read into it - those are the 160
		// BLANK-Actions rows (bow attacks, `firebreath`, `PowerAttack`, the mounted charges, the racial dances),
		// which carry no marker at all.
		NpcCombo,

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
