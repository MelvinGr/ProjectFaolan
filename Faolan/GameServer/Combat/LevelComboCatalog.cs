namespace Faolan.GameServer.Combat
{
	// The combos the client's combo book gains at a given level, sent as ComboBookIIR (0xA8BBEB7F) on a level-up ding
	// (#209). These are LEVEL-keyed and class-INDEPENDENT: every class captured (Guardian, Conqueror, Assassin, Herald
	// mage) receives the SAME set at a given level - so this is a flat level->combos map, not per-class. Harvested
	// verbatim from the login/gameplay captures (Hoxxling, Stihetfv, Strie level-up bursts); coverage is only the
	// levels present in captures (L2/L4/L5 so far). Levels with no entry grant no combos (a level-up sends none), which
	// matches retail (L3 dinged with zero ComboBook in the Hoxxling capture).
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
