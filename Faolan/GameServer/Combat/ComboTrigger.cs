namespace Faolan.GameServer.Combat
{
	/// <summary>
	///     How a catalog combo is triggered - the marker that prefixes <c>Combo.Actions</c>.
	/// </summary>
	/// <remarks>
	///     A combo either has a key sequence (<see cref="Directional" />, the bulk of the catalog) or carries one of
	///     these markers instead, in which case the single direction that follows the marker is a selector naming
	///     which of the family's five procs the row is.
	/// </remarks>
	public enum ComboTrigger
	{
		/// <summary>No marker: <c>Actions</c> is a key sequence the player performs (<c>UR,UP</c>).</summary>
		Directional,

		/// <summary>
		///     The bare <c>action</c> marker - 69 catalog rows, and the only marker with no direction after it.
		/// </summary>
		/// <remarks>
		///     The NPC combo set: all 69 rows are named <c>combo_npc_*</c> (<c>combo_npc_generic_polearm</c>,
		///     <c>combo_npc_guardian_polearm_1_sweep</c>, ...), and the corpus confirms it from the other side - those
		///     ids appear only s2c, with an NPC's actor handle, and never in a c2s request. There is no key sequence
		///     because there is no keyboard: the AI drives them.
		///     [V3: ComboWireTests.NpcComboRows_AreTheActionMarkerSet plus the corpus census in
		///     docs/subsystems/combos.md]
		///     Distinct from the combos the catalog does not model, which are the 160 blank-<c>Actions</c> rows (bow
		///     attacks, <c>firebreath</c>, <c>PowerAttack</c>, the mounted charges, the racial dances) and carry no
		///     marker at all.
		/// </remarks>
		NpcCombo,

		/// <summary>
		///     The caster set: a spellweave magic combo, whose five procs are its own (Exhaustion / Boiling Mana /
		///     Mind Purge / Mana Leech / Discourage).
		/// </summary>
		MagicCombo,

		/// <summary>
		///     One of the three reactive families - what has to happen for the proc to fire.
		/// </summary>
		/// <remarks>
		///     All three share one effect per direction (Debilitating Cleave / Rage / Furious Blow / Clumsy / Weaken),
		///     so the family is the trigger and the direction is the effect.
		/// </remarks>
		MagicReactive,
		MeleeReactive,
		ComboReactive
	}
}
