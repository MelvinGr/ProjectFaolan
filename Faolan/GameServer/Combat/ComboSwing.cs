using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Codecs.Combat;

namespace Faolan.GameServer.Combat
{
	/// <summary>
	///     One melee swing that the client attributed to a combo.
	/// </summary>
	/// <remarks>
	///     The client does not leave the server to infer which combo a key sequence matched: every <c>Combat:Melee</c>
	///     (BehaviorId 0x0F) it sends carries the combo in its CommandMessage args - <c>combo_id</c> (field 7) is the
	///     <c>Combos</c> catalog id verbatim and <c>combo_attack_num</c> (field 8) is the 0-based step within it,
	///     absent on the opener. So this is a read of the request rather than a reconstruction of it.
	///     [V3: 840 captured records across 26 captures resolve to 60 distinct catalog ids with no unknowns -
	///     ComboWireTests.CapturedComboSwings_AllResolveToTheCatalog]
	/// </remarks>
	public sealed record ComboSwing(Combo Combo, int Step, int StepCount, uint ActivationCentiseconds, uint TotalCentiseconds,
		SwingDirection? Direction = null)
	{
		/// <summary>
		///     The last step of the sequence - the finisher, which carries the combo's effect.
		/// </summary>
		/// <remarks>
		///     The client makes the same test: <c>AttackCommandBase_c</c> compares its step (+0x50) against the combo
		///     data's step count minus one before it fires the finisher branch (FUN_007de070).
		/// </remarks>
		public bool IsFinisher => Step == StepCount - 1;

		/// <summary>
		///     The opener. Retail re-sends step 0 when the player restarts a sequence, so this means "this swing began
		///     an attempt", not "the first swing of a fight".
		/// </summary>
		public bool IsOpener => Step == 0;

		/// <summary>
		///     The combo's payload.
		/// </summary>
		/// <remarks>
		///     <c>Combo.GameActionId</c> resolves into the <c>Spells</c> catalog for 726 of the 744 shipped combos,
		///     under the combo's own display name (combo <c>Assassin_20b_..._Grim_Corruption_II</c> -> spell 149094
		///     "Grim Corruption II"), so a combo's damage, DoT and debuff are the spell's rather than a separate damage
		///     model.
		/// </remarks>
		public uint SpellId => Combo.GameActionId;

		/// <summary>
		///     The whole attempt's animation length in seconds - the sum of every step's <c>activation_time</c>, which
		///     is what the client's <c>BCTCombo::GetComboAnimationLength</c> sums over the step animations.
		/// </summary>
		/// <remarks>Used for the stamina cost, floored the same way the client floors it.</remarks>
		public float AnimationSeconds => TotalCentiseconds < ComboService.MinAnimationCentiseconds
			? ComboService.MinAnimationCentiseconds / 100f
			: TotalCentiseconds / 100f;
	}
}
