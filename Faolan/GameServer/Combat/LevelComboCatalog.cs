namespace Faolan.GameServer.Combat
{
	// RETIRED from the live grant path - superseded by the class<->combo ACCESS map (ClassCombo). It was LEVEL-keyed
	// and class-INDEPENDENT (every captured class got the SAME set at a level), which wrongly gave casters weapon
	// combos (a Necromancer "learned" Sweep/Slam every login). GrantCharacterCombos / the level-up dings now read
	// ClassCombo instead. Kept ONLY as the capture-derived level->combo-id reference (Hoxxling/Stihetfv/Strie level-up
	// bursts; L2/L4/L5) for populating ClassCombo from dumps later - it is no longer referenced by any live code.
	public static class LevelComboCatalog
	{
		private static readonly System.Collections.Generic.Dictionary<uint, uint[]> ByLevel = new()
		{
			[2] = [0x0035F6BE, 0x000218C6, 0x0035F7E1, 0x0035F6B5],
			[4] = [0x0035F91E, 0x0035F917, 0x0035F91D, 0x00021A95],
			[5] = [0x0030DCBC],
		};

		// The combos unlocked exactly AT `level` (empty if that level grants none / is uncaptured).
		public static uint[] ForLevel(uint level) => ByLevel.TryGetValue(level, out var combos) ? combos : [];
	}
}
