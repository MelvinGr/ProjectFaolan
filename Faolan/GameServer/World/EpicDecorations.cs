using Faolan.Core.Database.Tables;

namespace Faolan.GameServer.World
{
	/// <summary>
	///     The nameplate decoration retag an epic instance applies to the normal population.
	/// </summary>
	/// <remarks>
	///     <c>normalset</c> is the client's decoration-set selector - the skull is pinned to it, and its value 4 is the
	///     "Prey" label - and the back-to-back Spider Caverns captures show epic re-tagging it per creature class with
	///     no exceptions over all 171 spawns: regular mobs (npc_type 4) go from absent to 13, the elite frame normal
	///     content already uses on the Ymirish, and minions (npc_type 1) from 1 to 9.
	///     <para>
	///         Only what is measured: mini-bosses and bosses (npc_type 2 and 3) did not occur in the epic window, so
	///         they keep their stored set rather than getting an invented one. Health is deliberately not retuned - the
	///         measured factor of roughly 8 is not a flat multiplier and one dungeon cannot pin the rule - so an epic
	///         copy serves the normal stats with the elite decorations.
	///     </para>
	/// </remarks>
	public static class EpicDecorations
	{
		// The elite decoration set (npc_type 4 regulars in the epic capture; the Ymirish in normal content).
		public const uint EliteNormalset = 13;

		// The epic minion decoration set (npc_type 1 in the epic capture).
		public const uint EpicMinionNormalset = 9;

		/// <summary>
		///     The <c>normalset</c> an epic copy serves for this creature, or null to keep the stored one.
		/// </summary>
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
