namespace Faolan.GameServer.Combat
{
	/// <summary>
	///     Provisional loot rules: a kill has a chance to drop a loot bag, which is a world object spawned at the death
	///     spot that expires after a timeout.
	/// </summary>
	/// <remarks>
	///     Drop chance, contents and per-NPC loot tables are not modelled from captured data, so this is a coin flip.
	///     The roll is passed in, which keeps the decision pure and testable.
	/// </remarks>
	public static class LootResolver
	{
		/// <summary>Chance a kill drops a loot bag.</summary>
		public const double DefaultDropChance = 0.5;

		/// <summary>
		///     Coins a dropped bag carries (tin, the wallet wire unit), scaled by the NPC's level.
		/// </summary>
		/// <remarks>
		///     The retail beach bags consistently granted 2 tin per loot - the +2 steps in the 0x2005 wallet captures,
		///     against level 2-5 NPCs. Higher-level values are extrapolated rather than measured. Pure: the caller
		///     supplies the roll.
		/// </remarks>
		public static uint RollCash(double roll, uint npcLevel)
		{
			var max = 1 + npcLevel; // level 2 -> 1..3 tin, centred near the observed 2
			return (uint)(1 + roll * max);
		}

		/// <summary>True when <paramref name="roll" />, expected in [0,1), is under the drop chance.</summary>
		public static bool RollsDrop(double roll, double dropChance = DefaultDropChance)
		{
			return roll < dropChance;
		}
	}
}
