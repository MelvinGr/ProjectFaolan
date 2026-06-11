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
