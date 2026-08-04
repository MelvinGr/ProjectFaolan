using Faolan.Core.Database.Tables;

namespace Faolan.GameServer.Combat
{
	// One melee swing that the CLIENT attributed to a combo. The client does not leave the server to infer which
	// combo a key sequence matched: every `Combat:Melee` (BehaviorId 0x0F) it sends carries the combo in its
	// CommandMessage args - `combo_id` (field 7) is the `Combos` catalog id verbatim and `combo_attack_num`
	// (field 8) is the 0-based step within it, absent on the opener. So this is a READ of the request, not a
	// reconstruction of it.
	// [V3: 840 captured records across 26 captures resolve to 60 distinct catalog ids with ZERO unknowns -
	// ComboWireTests.CapturedComboSwings_AllResolveToTheCatalog]
	public sealed record ComboSwing(Combo Combo, int Step, int StepCount, uint ActivationCentiseconds, uint TotalCentiseconds)
	{
		// The last step of the sequence - the finisher, which is what carries the combo's effect. The client makes
		// the same test: `AttackCommandBase_c` compares its step (+0x50) against the combo data's step count minus
		// one before it fires the finisher branch (FUN_007de070).
		public bool IsFinisher => Step == StepCount - 1;

		// The opener. Retail re-sends step 0 when the player restarts a sequence, so this is not "the first swing
		// of a fight" - it is "this swing began an attempt".
		public bool IsOpener => Step == 0;

		// The combo's payload. `Combo.GameActionId` resolves into the `Spells` catalog for 726 of the 744 shipped
		// combos, under the combo's OWN display name (combo `Assassin_20b_..._Grim_Corruption_II` -> spell 149094
		// "Grim Corruption II"), so a combo's damage, DoT and debuff are the spell's - not a separate damage model.
		public uint SpellId => Combo.GameActionId;

		// The whole attempt's animation length in seconds - the sum of every step's `activation_time`, which is what
		// the client's own `BCTCombo::GetComboAnimationLength` sums (over the step animations). Used for the stamina
		// cost; floored the same way the client floors it.
		public float AnimationSeconds => TotalCentiseconds < ComboService.MinAnimationCentiseconds
			? ComboService.MinAnimationCentiseconds / 100f
			: TotalCentiseconds / 100f;
	}
}
