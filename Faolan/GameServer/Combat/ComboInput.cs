using Faolan.Core.Network.Codecs.Combat;

namespace Faolan.GameServer.Combat
{
	/// <summary>
	///     The canonical combat-input directions a combo step requires.
	/// </summary>
	/// <remarks>
	///     AoC combos are a sequence of directional melee swings the player performs in order (<c>Combo.Actions</c>,
	///     e.g. "UR,UP"); completing the sequence fires the finisher. The five directional keys - the four quadrants
	///     plus overhead - dominate the catalog, while Head and Thrust are rare specials. This enum is the catalog's
	///     vocabulary; <see cref="SwingDirection" /> is the wire's, and <see cref="ComboInputs.ToSwingDirection" /> is
	///     the bridge.
	/// </remarks>
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
		/// <summary>
		///     The wire zone a catalog step keys to, or null when the step is not one of the five - <c>Thrust</c> is a
		///     distinct special rather than a direction of swing.
		/// </summary>
		/// <remarks>
		///     <c>Up</c> and <c>Head</c> are the same zone: the client's config key for the overhead direction is
		///     <c>attack_dir_bm_head</c> (<c>FUN_0152f740</c>, the block whose order gives
		///     <see cref="SwingDirection" />), so the catalog's two spellings are two names for the overhead key rather
		///     than two keys. They remain separate enum members because the catalog spells both and the parser must
		///     accept both.
		/// </remarks>
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
