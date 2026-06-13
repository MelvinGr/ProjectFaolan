using System.ComponentModel;
using System.Text.Json;
using System.Threading.Tasks;
using Faolan.Core.Network.GameServer;
using ModelContextProtocol.Server;

namespace Faolan.Mcp.Tools
{
	/// <summary>
	///     Tools that drive the live world. Backed by <see cref="IGameServerDispatcher" />, which aggregates the
	///     creator + world GameServer listeners and routes actions through the same already-validated GM command
	///     paths a game master uses (spawn, teleport, …). Mutating tools require an explicit confirm flag.
	/// </summary>
	[McpServerToolType]
	public sealed class GameServerTools
	{
		private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { WriteIndented = true };

		private readonly IGameServerDispatcher _dispatcher;

		public GameServerTools(IGameServerDispatcher dispatcher)
		{
			_dispatcher = dispatcher;
		}

		[McpServerTool(Name = "list_world_sessions"), Description("List clients connected to the live GameServer (world) listeners, with account/character identity and source ip. Read-only.")]
		public string ListWorldSessions()
		{
			var sessions = _dispatcher.GetSessions();
			return sessions.Count == 0
				? "No clients are currently connected to the GameServer."
				: JsonSerializer.Serialize(sessions, JsonOptions);
		}

		[McpServerTool(Name = "run_gm_command"),
		 Description(
			 "Run a GM command for a connected character on the LIVE server, exactly as if the player typed it in chat (e.g. \"teleport 5\", \"test\" to spawn the test NPC, \"help\" to list commands). MUTATING: set confirm=true to actually run it.")]
		public async Task<string> RunGmCommand([Description("Character id of a currently-connected player (see list_world_sessions)")] uint characterId,
			[Description("GM command text, with or without the leading '.' (e.g. \"teleport 5\")")] string command, [Description("Must be true to execute. When false, the command is described but not run.")] bool confirm = false)
		{
			if (!confirm)
			{
				return $"Would run GM command '{command}' for character {characterId} on the live server. " + "This is a mutating action — call again with confirm=true to execute.";
			}

			var ok = await _dispatcher.RunGmCommand(characterId, command);
			return ok
				? $"Ran GM command '{command}' for character {characterId}."
				: $"Character {characterId} is not currently connected to any GameServer listener.";
		}

		[McpServerTool(Name = "list_zone_entities"),
		 Description(
			 "Dump the live world model for a map: every entity in the zone (players, NPCs, loot bags) with handle, level, vitals, hostility and position. The feedback loop for spawn/combat/loot work. Read-only; empty until a player has entered the map this run.")]
		public string ListZoneEntities([Description("Map id (see list_maps)")] uint mapId)
		{
			var entities = _dispatcher.GetZoneEntities(mapId);
			return entities.Count == 0
				? $"No live entities on map {mapId} (zone not loaded this run, or empty)."
				: JsonSerializer.Serialize(entities, JsonOptions);
		}

		[McpServerTool(Name = "despawn_entity"),
		 Description(
			 "Despawn a non-player entity (NPC, loot bag) by wire handle in a connected character's zone - removed from every nearby client and from the world model (wrapper over the .despawnnpc GM command; handles come from list_zone_entities). MUTATING: set confirm=true to actually despawn.")]
		public async Task<string> DespawnEntity([Description("Character id of a currently-connected player whose zone holds the entity")] uint characterId,
			[Description("Entity wire handle, 0x-hex (e.g. \"0x00090001\") or decimal")] string entityHandle, [Description("Must be true to execute.")] bool confirm = false)
		{
			if (!confirm)
				return $"Would despawn entity {entityHandle} in character {characterId}'s zone. Call again with confirm=true to execute.";

			var ok = await _dispatcher.RunGmCommand(characterId, $".despawnnpc {entityHandle}");
			return ok
				? $"Ran despawn of {entityHandle} in character {characterId}'s zone (the in-game system-message reply states whether the handle was found)."
				: $"Character {characterId} is not currently connected to any GameServer listener.";
		}

		[McpServerTool(Name = "add_item"),
		 Description(
			 "Add an item by faolan.db item id to a connected character's bag - persisted AND delivered LIVE via the DispatchAddItem (0x2020) packet, so it appears in the bag without a relog. The item must be snapshot-buildable (Archetype != 0). Wrapper over the .additem GM command; find ids with search_items. MUTATING: set confirm=true to actually add.")]
		public async Task<string> AddItem([Description("Character id of a currently-connected player")] uint characterId, [Description("Item id (see search_items / get_item)")] uint itemId,
			[Description("Stack count (default 1)")] uint count = 1, [Description("Must be true to execute.")] bool confirm = false)
		{
			if (!confirm)
				return $"Would add {count}x item {itemId} to character {characterId}'s bag (live DispatchAddItem). Call again with confirm=true to execute.";

			var ok = await _dispatcher.RunGmCommand(characterId, $".additem {itemId} {count}");
			return ok
				? $"Ran add of {count}x item {itemId} for character {characterId} (the in-game system-message reply states the bag slot, or why it failed)."
				: $"Character {characterId} is not currently connected to any GameServer listener.";
		}

		[McpServerTool(Name = "spawn_bag"),
		 Description(
			 "Drop a loot bag at a connected character's feet with a couple of random items + coins (the same bag an NPC kill can drop). Open it in-client and \"loot all\" to grant the items live (DispatchAddItem) and the coins. Wrapper over the .spawnbag GM command. MUTATING: set confirm=true to actually spawn.")]
		public async Task<string> SpawnBag([Description("Character id of a currently-connected player (see list_world_sessions)")] uint characterId,
			[Description("Must be true to execute.")] bool confirm = false)
		{
			if (!confirm)
				return $"Would drop a loot bag with random items + coins at character {characterId}'s position. Call again with confirm=true to execute.";

			var ok = await _dispatcher.RunGmCommand(characterId, ".spawnbag");
			return ok
				? $"Ran .spawnbag for character {characterId} (the in-game system-message reply lists the bag handle + items, or why it failed)."
				: $"Character {characterId} is not currently connected to any GameServer listener.";
		}

		[McpServerTool(Name = "adjust_money"),
		 Description("Set or add to a connected character's wallet, in tin, the game's smallest coin (wrapper over the .money GM command; 100 tin = 1 copper, 1000000 tin = 1 gold). The client HUD updates live. MUTATING: set confirm=true to actually change it.")]
		public async Task<string> AdjustMoney([Description("Character id of a currently-connected player")] uint characterId, [Description("Amount in tin")] ulong tin,
			[Description("true = add to the balance, false = set the balance")] bool add = true, [Description("Must be true to execute.")] bool confirm = false)
		{
			if (!confirm)
				return $"Would {(add ? "add" : "set")} {tin} tin {(add ? "to" : "as")} character {characterId}'s wallet. Call again with confirm=true to execute.";

			var ok = await _dispatcher.RunGmCommand(characterId, add
				? $".money +{tin}"
				: $".money {tin}");
			return ok
				? $"{(add ? "Added" : "Set")} {tin} tin {(add ? "to" : "as")} character {characterId}'s wallet."
				: $"Character {characterId} is not currently connected to any GameServer listener.";
		}

		[McpServerTool(Name = "manage_quest"),
		 Description("Add or remove a quest (by RDB quest id) in a connected character's journal (wrapper over the .quest GM command; quest text renders from the client's RDB). Current quests: see get_character. MUTATING: set confirm=true to actually change it.")]
		public async Task<string> ManageQuest([Description("Character id of a currently-connected player")] uint characterId, [Description("\"add\" or \"remove\"")] string action,
			[Description("RDB quest id (e.g. 13479)")] uint questId, [Description("Must be true to execute.")] bool confirm = false)
		{
			action = action?.Trim().ToLowerInvariant() ?? "";
			if (action is not ("add" or "remove"))
				return "Action must be \"add\" or \"remove\".";
			if (!confirm)
				return $"Would {action} quest {questId} {(action == "add" ? "to" : "from")} character {characterId}'s journal. Call again with confirm=true to execute.";

			var ok = await _dispatcher.RunGmCommand(characterId, $".quest {action} {questId}");
			return ok
				? $"Ran quest {action} of {questId} for character {characterId}."
				: $"Character {characterId} is not currently connected to any GameServer listener.";
		}

		[McpServerTool(Name = "add_tag"),
		 Description("Set a character tag (the Lua/dialogue state flags) on a connected character (wrapper over the .tag GM command). Current tags: see get_character. MUTATING: set confirm=true to actually set it.")]
		public async Task<string> AddTag([Description("Character id of a currently-connected player")] uint characterId, [Description("Numeric tag id")] uint tagId,
			[Description("Must be true to execute.")] bool confirm = false)
		{
			if (!confirm)
				return $"Would set tag {tagId} on character {characterId}. Call again with confirm=true to execute.";

			var ok = await _dispatcher.RunGmCommand(characterId, $".tag add {tagId}");
			return ok
				? $"Set tag {tagId} on character {characterId}."
				: $"Character {characterId} is not currently connected to any GameServer listener.";
		}

		[McpServerTool(Name = "spawn_npc"),
		 Description(
			 "Spawn a captured NPC by name (case-insensitive) at a connected character's position (convenience wrapper over the .spawnnpc GM command). The NPC renders for nearby players and is killable/hostile per its captured row. Discover names via get_map_npc_spawns. MUTATING: set confirm=true to actually spawn.")]
		public async Task<string> SpawnNpc([Description("Character id of a currently-connected player (see list_world_sessions)")] uint characterId,
			[Description("NPC name as captured, case-insensitive (e.g. \"Crocodile\"); see get_map_npc_spawns for names")] string npcName,
			[Description("Must be true to execute.")] bool confirm = false)
		{
			if (!confirm)
				return $"Would spawn NPC '{npcName}' at character {characterId}'s position. Call again with confirm=true to execute.";

			var ok = await _dispatcher.RunGmCommand(characterId, $".spawnnpc {npcName}");
			return ok
				? $"Ran spawn of '{npcName}' at character {characterId}'s position (the in-game system-message reply states whether a matching captured NPC row was found)."
				: $"Character {characterId} is not currently connected to any GameServer listener.";
		}

		[McpServerTool(Name = "teleport_character"), Description("Teleport a connected character to a map by id (convenience wrapper over the .teleport GM command). MUTATING: set confirm=true to actually teleport.")]
		public async Task<string> TeleportCharacter([Description("Character id of a currently-connected player")] uint characterId, [Description("Destination map id (see list_maps)")] uint mapId,
			[Description("Must be true to execute.")] bool confirm = false)
		{
			if (!confirm)
				return $"Would teleport character {characterId} to map {mapId}. Call again with confirm=true to execute.";

			var ok = await _dispatcher.RunGmCommand(characterId, $".teleport {mapId}");
			return ok
				? $"Teleported character {characterId} to map {mapId}."
				: $"Character {characterId} is not currently connected to any GameServer listener.";
		}
	}
}
