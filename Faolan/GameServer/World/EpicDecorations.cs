using Faolan.Core.Database.Tables;

namespace Faolan.GameServer.World
{
	// The nameplate DECORATION retag an EPIC instance applies to the normal population (#309). `normalset` is the
	// client's decoration-set selector (#508 pinned the skull to it; #758's "Prey" label is its value 4), and the
	// back-to-back Spider Caverns captures show epic re-tags it per creature CLASS with zero exceptions over all
	// 171 spawns: regular mobs (npc_type 4) go absent -> 13 - the ELITE frame normal content already uses on the
	// Ymirish - and minions (npc_type 1) 1 -> 9.
	//
	// ⚠️ Only what is measured: mini-bosses and bosses (npc_type 2/3) did not occur in the epic window, so they
	// keep their stored set rather than getting an invented one. Health is deliberately NOT retuned (the measured
	// ~8x is not a flat multiplier and one dungeon cannot pin the rule) - an epic copy serves the normal stats
	// with the elite decorations, per the #309 scoping call.
	public static class EpicDecorations
	{
		// The elite decoration set (npc_type 4 regulars in the epic capture; the Ymirish in normal content).
		public const uint EliteNormalset = 13;

		// The epic minion decoration set (npc_type 1 in the epic capture).
		public const uint EpicMinionNormalset = 9;

		// The `normalset` an epic copy serves for this creature, or null to keep the stored one.
		public static uint? NormalsetFor(NpcStatCore? core)
		{
			return core?.NpcType switch
			{
				4 => EliteNormalset,
				1 => EpicMinionNormalset,
				_ => null
			};
		}
	}
}
