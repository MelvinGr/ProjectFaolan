namespace Faolan.GameServer.Combat
{
	// Provisional loot rules (docs/archive/server-architecture-roadmap.md). A kill has a chance to drop a loot bag; the bag
	// is a world object spawned at the death spot that expires after a timeout. Drop chance / contents / per-NPC loot
	// tables aren't modelled from real data yet (like the damage + XP values), so this is a simple coin-flip; the roll
	// is passed in so the decision is pure + testable (the caller supplies the RNG).
	public static class LootResolver
	{
		// Chance a kill drops a loot bag.
		public const double DefaultDropChance = 0.5;

		// How long a dropped loot bag stays in the world before it expires and despawns.
		public const double LootExpirySeconds = 120.0;

		// True if `roll` (expected in [0,1)) is under the drop chance.
		public static bool RollsDrop(double roll, double dropChance = DefaultDropChance)
		{
			return roll < dropChance;
		}
	}
}
