namespace Faolan.Core.Enums
{
	// The client's map-pin TYPE - the value of `CharacterStatId.WaypointType` (0xAC) on a dynel, and the type word of
	// every waypoint record the map GUI holds (#921).
	//
	// **Recovered from `RMWaypoint_c`'s constructor** (`FUN_01727820`, `MapGUI\RMWaypoint.cpp`): a dense switch over
	// the type word that assigns each pin its bitmaps, so each member below is named for the bitmap its case loads
	// (`WorldmapGUI/minimap_<name>.tga`), never for what a pin looked like in a capture. Values 6 and 14 fall to the
	// switch's default and draw nothing.
	//
	// ⚠️ Two values the switch special-cases AHEAD of the table are deliberately not members: 0xC748 (a zone label's
	// travel pin, `minimap_travel`) and 0xC87B (a tombstone, `minimap_tombstone` + `_direction`). They are the
	// dynel's own CLASS words - `EntityClass.ZoneLabel` and `EntityClass.TombStone` in Faolan.Core.Network - so the
	// 104 zone-label rows of `WorldObjects.WaypointType` hold 51016 = their class, not a type from this table.
	//
	// A DYNEL may carry only the subset its stat-apply accepts (`FUN_0080bb20`: 0x0F-0x12, 0x1A, 0x1B, 0x1D, 0xC748 -
	// anything else logs "The waypoint type stat has a strange value."). That subset is the harvest-node icons plus
	// the massive-PvP flag and the travel point, and it is what `WorldObjects.WaypointType` holds: a rock is
	// `StoneNode`, a tree `WoodNode`, a vein `GoldNode`, a deposit `IronNode`, a cotton shrub `ClothNode`, a zone
	// label its own class word (see above). `ResourceGatheringActive` is the one value never stored: retail pushes it onto a node for the
	// duration of a harvesting bout and then restores the node's own kind - see docs/re/waypoint-type-stat.md.
	public enum WaypointType : uint
	{
		None = 0,
		Self = 1, // minimap_arrowself + minimap_arrowself_circle - the player's own arrow
		Player = 2, // minimap_player + minimap_playerdirection - another player
		Shop = 3,
		QuestGiver = 4,
		QuestDone = 5,
		ResurrectionPoint = 7,
		PointOfInterest = 8, // minimap_marker_green - the legend's "POI"
		Custom = 9, // minimap_marker - the legend's "Custom waypoint"
		CtfFlagRed = 10,
		CtfFlagBlue = 11,
		Tracking = 12,
		Playfield = 13, // worldmap_playfield / _premium, with locked/unlocked overlays by the player's access state
		StoneNode = 15, // minimap_stone - the rock harvest nodes (Basalt, Granite, Sandstone, Slate, ...)
		WoodNode = 16, // minimap_wood - the trees (Ash, Birch, Elm, Yew, ...)
		GoldNode = 17, // minimap_gold - the lodes and veins (Bismuth, Electrum, Gold, Pyrite, Silver, ...)
		IronNode = 18, // minimap_iron - the deposits and ore nodes (Chromium, Copper, Iron, Lead, Nickel, ...)
		DungeonIcon = 19, // worldmap_icon_dungeon3
		Travel = 20, // minimap_travel - the legend's "Travel"
		Dungeon = 21, // worldmap_dungeon, with locked/unlocked overlays by the player's access state
		Hub = 22, // worldmap_hub / _premium, likewise
		Npc = 23, // minimap_npc - three consecutive values share the one bitmap
		Npc2 = 24,
		Npc3 = 25,
		MassivePvp = 26, // minimap_massivepvp_active
		ClothNode = 27, // minimap_cloth - the cotton shrubs
		MurdererResurrectionPoint = 28, // minimap_resurrectionpoint_red
		ResourceGatheringActive = 29, // minimap_resourcegathering_active - a harvest node someone is working
		QuestLocation = 30, // the six quest location/direction/region bitmaps, untracked
		QuestLocationTracked = 31, // ... tracked
		QuestLocationSelected = 32, // ... selected
		OnslaughtEnemySpawn = 33,
		OnslaughtEnemy = 34,
		OnslaughtWildlifeCamp = 35,
		OnslaughtLootMummy = 36,
		OnslaughtTreasureChest = 37,
		OnslaughtBoss = 38
	}
}
