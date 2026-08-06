using Faolan.Core.Network.Codecs.Combat;

namespace Faolan.GameServer.Combat
{
	// The canonical combat-input directions a combo step requires. AoC combos are a SEQUENCE of directional melee
	// swings the player performs in order (Combo.Actions, e.g. "UR,UP"); completing the sequence fires the finisher.
	// The five directional keys (the four quadrants + overhead) dominate the catalog; Head/Thrust are rare specials.
	// This enum is the CATALOG's vocabulary - `SwingDirection` is the WIRE's, and `ComboInputs.ToSwingDirection`
	// is the bridge.
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

	public static class ComboInputs
	{
		// The wire zone a catalog step keys to, or null when the step is not one of the five (`Thrust`, which is a
		// distinct special rather than a direction of swing).
		//
		// ⚠️ `Up` and `Head` are the SAME zone. The client's own config key for the overhead direction is
		// `attack_dir_bm_head` (`FUN_0152f740`, the block whose order gives `SwingDirection`) - so the catalog's two
		// spellings are two names for the overhead key, not two keys. They stayed separate enum members because the
		// catalog spells both and the parser must accept both.
		public static SwingDirection? ToSwingDirection(this ComboInput input)
		{
			return input switch
			{
				ComboInput.UpperLeft => SwingDirection.UpperLeft,
				ComboInput.UpperRight => SwingDirection.UpperRight,
				ComboInput.LowerLeft => SwingDirection.LowerLeft,
				ComboInput.LowerRight => SwingDirection.LowerRight,
				ComboInput.Up or ComboInput.Head => SwingDirection.Overhead,
				_ => null
			};
		}
	}
}
