namespace Faolan.Core.Enums
{
	/// <summary>
	///     The client's map-pin type - the value of <c>CharacterStatId.WaypointType</c> (0xAC) on a dynel, and the
	///     type word of every waypoint record the map GUI holds.
	/// </summary>
	/// <remarks>
	///     Recovered from <c>RMWaypoint_c</c>'s constructor (FUN_01727820, <c>MapGUI\RMWaypoint.cpp</c>): a dense
	///     switch over the type word that assigns each pin its bitmaps, so each member below is named for the bitmap
	///     its case loads (<c>WorldmapGUI/minimap_&lt;name&gt;.tga</c>), never for what a pin looked like in a
	///     capture. Values 6 and 14 fall to the switch's default and draw nothing.
	///     <para />
	///     Two values the switch special-cases ahead of the table are deliberately not members: 0xC748 (a zone label's
	///     travel pin, <c>minimap_travel</c>) and 0xC87B (a tombstone, <c>minimap_tombstone</c> plus
	///     <c>_direction</c>). They are the dynel's own class words - <c>EntityClass.ZoneLabel</c> and
	///     <c>EntityClass.TombStone</c> in Faolan.Core.Network - so the 104 zone-label rows of
	///     <c>WorldObjects.WaypointType</c> hold 51016 = their class, not a type from this table.
	///     <para />
	///     A dynel may carry only the subset its stat-apply accepts (FUN_0080bb20: 0x0F-0x12, 0x1A, 0x1B, 0x1D,
	///     0xC748 - anything else logs "The waypoint type stat has a strange value."). That subset is the
	///     harvest-node icons plus the massive-PvP flag and the travel point, and it is what
	///     <c>WorldObjects.WaypointType</c> holds: a rock is <see cref="StoneNode" />, a tree <see cref="WoodNode" />,
	///     a vein <see cref="GoldNode" />, a deposit <see cref="IronNode" />, a cotton shrub <see cref="ClothNode" />,
	///     a zone label its own class word. <see cref="ResourceGatheringActive" /> is the one value never stored:
	///     retail pushes it onto a node for the duration of a harvesting bout and then restores the node's own kind -
	///     see <c>docs/re/waypoint-type-stat.md</c>.
	/// </remarks>
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
