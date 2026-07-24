using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Faolan.Core.Network.Server.GameServer;
using ModelContextProtocol.Server;

namespace Faolan.Mcp.Tools
{
	/// <summary>
	///     Tools that drive the live world. Backed by <see cref="IGameServerDispatcher" />, the seam onto this
	///     process's GameServer listener, which routes actions through the same already-validated GM command
	///     paths a game master uses (spawn, teleport, …).
	/// </summary>
	[McpServerToolType]
	public sealed class GameServerTools
	{
		private static readonly JsonSerializerOptions JsonOptions = new()
		{
			WriteIndented = true
		};

		// The full-session dump (list_world_sessions) walks the live INetworkClientSession, whose Account/Character are
		// EF entities with navigation cycles - IgnoreCycles keeps the serializer from throwing on those back-references.
		private static readonly JsonSerializerOptions SessionJsonOptions = new()
		{
			WriteIndented = true,
			ReferenceHandler = ReferenceHandler.IgnoreCycles
		};

		private readonly IGameServerDispatcher _dispatcher;

		public GameServerTools(IGameServerDispatcher dispatcher)
		{
			_dispatcher = dispatcher;
		}

		[McpServerTool(Name = "add_item"),
		 Description(
			 "Add an item by faolan.db item id to a connected character's bag - persisted AND delivered LIVE via the DispatchAddItem (0x2020) packet, so it appears in the bag without a relog. The item must be snapshot-buildable (Archetype != 0). Wrapper over the .additem GM command; find ids with search_items.")]
		public async Task<string> AddItem([Description("Character id of a currently-connected player")] uint characterId, [Description("Item id (see search_items / get_item)")] uint itemId,
			[Description("Stack count (default 1)")] uint count = 1)
		{
			var ok = await _dispatcher.RunGmCommand(characterId, $".additem {itemId} {count}");
			return ok
				? $"Ran add of {count}x item {itemId} for character {characterId} (the in-game system-message reply states the bag slot, or why it failed)."
				: $"Character {characterId} is not currently connected to any GameServer listener.";
		}

		[McpServerTool(Name = "kick_player"),
		 Description("Force-disconnect a connected character from the live world (closes their world socket). Used by the web admin ban+kick. Returns whether a live session was closed.")]
		public string KickPlayer([Description("Character id of a currently-connected player (see list_world_sessions)")] uint characterId)
		{
			return _dispatcher.DisconnectCharacter(characterId)
				? $"Disconnected character {characterId} from the world."
				: $"Character {characterId} is not currently connected to any GameServer listener.";
		}

		[McpServerTool(Name = "kill"),
		 Description(
			 "Kill an entity in a connected character's zone (wrapper over the .kill GM command). Omit the handle to kill the character's currently selected target (their last in-game LookAt); pass a handle (from list_zone_entities) to kill a specific NPC; pass \"self\" to kill the player themselves (they respawn at the nearest resurrection point). An NPC runs the full death lifecycle (despawn/corpse-loot, XP, kill-quest advance).")]
		public async Task<string> Kill([Description("Character id of a currently-connected player")] uint characterId,
			[Description("Optional target: an entity wire handle (0x-hex or decimal), \"self\", or empty for the character's selected target")] string target = "")
		{
			var ok = await _dispatcher.RunGmCommand(characterId, $".kill {target}".TrimEnd());
			return ok
				? $"Ran kill ({(string.IsNullOrWhiteSpace(target) ? "selected target" : target)}) for character {characterId} (the in-game system-message reply states the outcome)."
				: $"Character {characterId} is not currently connected to any GameServer listener.";
		}

		[McpServerTool(Name = "list_world_sessions"),
		 Description(
			 "List clients connected to the live GameServer (world) listeners, dumping each client's COMPLETE session state (selected target handle, energy, conversation, entry mode, account + character, …), not just the identity summary. Read-only. Pass `filter` to narrow to sessions whose character name, account name, or character/account id matches (case-insensitive substring).")]
		public string ListWorldSessions([Description("Optional case-insensitive filter on character name, account name, or character/account id; empty = all sessions.")] string filter = "")
		{
			var sessions = _dispatcher.GetWorldSessions();
			if (!string.IsNullOrWhiteSpace(filter))
			{
				var f = filter.Trim();
				sessions = sessions.Where(s =>
					Contains(s.Character?.Name, f) || Contains(s.Account?.UserName, f) || Contains(s.Character?.Id.ToString(CultureInfo.InvariantCulture), f) ||
					Contains(s.Account?.Id.ToString(CultureInfo.InvariantCulture), f));
			}

			var s = sessions.ToArray();

			return s.Length == 0
				? string.IsNullOrWhiteSpace(filter)
					? "No clients are currently connected to the GameServer."
					: $"No connected session matches filter '{filter}'."
				: JsonSerializer.Serialize(s, SessionJsonOptions);
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

		[McpServerTool(Name = "run_gm_command"),
		 Description("Run a GM command for a connected character on the server, exactly as if the player typed it in chat (e.g. \"teleport 5\", \"help\" to list the registered commands).")]
		public async Task<string> RunGmCommand([Description("Character id of a currently-connected player (see list_world_sessions)")] uint characterId,
			[Description("GM command text, with or without the leading '.' (e.g. \"teleport 5\")")] string command)
		{
			var ok = await _dispatcher.RunGmCommand(characterId, command);
			return ok
				? $"Ran GM command '{command}' for character {characterId}."
				: $"Character {characterId} is not currently connected to any GameServer listener.";
		}

		[McpServerTool(Name = "teleport_character"),
		 Description(
			 "Teleport a connected character (convenience wrapper over the .teleport GM command). Either give a destination map id, OR give x/y/z to move to a position on the character's CURRENT map (no map change). Map id takes priority if both are supplied.")]
		public async Task<string> TeleportCharacter([Description("Character id of a currently-connected player")] uint characterId,
			[Description("Destination map id (see list_maps); omit to teleport by position instead")] uint? mapId = null,
			[Description("X coordinate on the current map (requires y and z; ignored if mapId is given)")] float? x = null,
			[Description("Y coordinate on the current map (requires x and z; ignored if mapId is given)")] float? y = null,
			[Description("Z coordinate on the current map (requires x and y; ignored if mapId is given)")] float? z = null)
		{
			string command, success;
			if (mapId is { } map)
			{
				command = $".teleport {map}";
				success = $"Teleported character {characterId} to map {map}.";
			}
			else if (x is { } px && y is { } py && z is { } pz)
			{
				var i = CultureInfo.InvariantCulture;
				command = $".teleport {px.ToString(i)} {py.ToString(i)} {pz.ToString(i)}";
				success = $"Teleported character {characterId} to ({px.ToString(i)}, {py.ToString(i)}, {pz.ToString(i)}) on its current map.";
			}
			else
				return "Supply either a mapId, or all three of x/y/z to teleport by position.";

			var ok = await _dispatcher.RunGmCommand(characterId, command);
			return ok
				? success
				: $"Character {characterId} is not currently connected to any GameServer listener.";
		}

		private static bool Contains(string? haystack, string needle)
		{
			return haystack != null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
		}
	}
}
