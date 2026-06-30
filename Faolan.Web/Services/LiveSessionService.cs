using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Faolan.Web.Services
{
	// One live connected player, as reported by the game server's MCP `list_sessions` tool. Mirrors
	// Faolan.Core's SessionInfo record (we deserialize the tool's JSON rather than referencing the server types).
	public sealed record LiveSession(
		uint? CharacterId, string? CharacterName, ushort? CharacterLevel, uint? AccountId, string? AccountName);

	// One live entity in a zone (player/npc/loot), as reported by the MCP list_zone_entities tool. Mirrors
	// Faolan.Core's ZoneEntityInfo. X/Z are the ground plane (Y is height) — the radar plots X vs Z.
	public sealed record ZoneEntity(
		uint Handle, string Name, string Kind, uint Level, bool Hostile, bool Dead,
		uint? Health, uint? MaxHealth, float X, float Y, float Z);

	// Queries the RUNNING game server's in-process MCP endpoint (http://127.0.0.1:{McpPort}/mcp) for live presence.
	// Faolan.Web is a separate process, so the only window into the server's in-memory sessions is this MCP call
	// (DB-only data can't see who is connected right now). Returns null when the server/MCP endpoint is unreachable
	// (server offline) so the page can distinguish "offline" from "online, nobody connected".
	public sealed class LiveSessionService
	{
		private readonly Uri _endpoint;
		private readonly ILogger<LiveSessionService> _logger;
		private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

		public LiveSessionService(IConfiguration config, ILogger<LiveSessionService> logger)
		{
			_logger = logger;
			// Default to the game server's documented MCP port; overridable so a non-default deployment still resolves.
			var port = config["McpPort"] ?? "7099";
			_endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
		}

		// Live sessions, or null if the server's MCP endpoint can't be reached (treat as "server offline").
		public async Task<IReadOnlyList<LiveSession>?> GetSessionsAsync(CancellationToken ct = default)
		{
			var text = await CallAsync("list_sessions", null, ct);
			if (text is null)
				return null; // unreachable / offline

			// The tool returns a human string when empty, else a JSON array of SessionInfo.
			if (!text.TrimStart().StartsWith('['))
				return [];
			try { return JsonSerializer.Deserialize<List<LiveSession>>(text, JsonOptions) ?? []; }
			catch { return []; }
		}

		// Live entities on a map (players/NPCs/loot with positions), or null if the server is unreachable. Empty list
		// when the zone isn't loaded this run.
		public async Task<IReadOnlyList<ZoneEntity>?> GetZoneEntitiesAsync(uint mapId, CancellationToken ct = default)
		{
			var text = await CallAsync("list_zone_entities",
				new Dictionary<string, object?> { ["mapId"] = mapId }, ct);
			if (text is null)
				return null;
			if (!text.TrimStart().StartsWith('['))
				return []; // "No live entities on map ..."
			try
			{
				var list = JsonSerializer.Deserialize<List<ZoneEntity>>(text, JsonOptions) ?? [];
				// The zone query unions every live instance of the map (#268), so each entity repeats per copy —
				// dedupe by handle for the radar.
				return list.GroupBy(e => e.Handle).Select(g => g.First()).ToList();
			}
			catch { return []; }
		}

		// Send an in-game system message (the yellow server text) to one connected character. Returns the tool's
		// status text, or null if the server is unreachable.
		public Task<string?> SendSystemMessageAsync(uint characterId, string message, CancellationToken ct = default)
			=> CallAsync("send_system_message",
				new Dictionary<string, object?> { ["characterId"] = characterId, ["message"] = message }, ct);

		// Run a GM command for a connected character, exactly as if typed in chat (e.g. "heal", "kill", "level 80").
		public Task<string?> RunGmCommandAsync(uint characterId, string command, CancellationToken ct = default)
			=> CallAsync("run_gm_command",
				new Dictionary<string, object?> { ["characterId"] = characterId, ["command"] = command }, ct);

		// Force-disconnect one connected character. Returns the tool's status text, or null if the server is offline.
		public Task<string?> KickPlayerAsync(uint characterId, CancellationToken ct = default)
			=> CallAsync("kick_player", new Dictionary<string, object?> { ["characterId"] = characterId }, ct);

		// Kick every live session belonging to an account (used by the admin ban+kick). Returns how many were
		// disconnected (0 when the account isn't online or the server is offline).
		public async Task<int> KickAccountAsync(uint accountId, CancellationToken ct = default)
		{
			var sessions = await GetSessionsAsync(ct);
			if (sessions is null)
				return 0;

			var kicked = 0;
			foreach (var s in sessions.Where(s => s.AccountId == accountId && s.CharacterId is not null))
				if (await KickPlayerAsync(s.CharacterId!.Value, ct) is { } r && r.StartsWith("Disconnected", StringComparison.Ordinal))
					kicked++;
			return kicked;
		}

		// Open a short-lived MCP client, call one tool, return its first text block (null = unreachable/failed).
		private async Task<string?> CallAsync(string tool, IReadOnlyDictionary<string, object?>? args, CancellationToken ct)
		{
			try
			{
				var transport = new HttpClientTransport(new HttpClientTransportOptions
				{
					Endpoint = _endpoint,
					Name = "faolan-web"
				});
				await using var client = await McpClient.CreateAsync(transport, cancellationToken: ct);
				var result = await client.CallToolAsync(tool, args, cancellationToken: ct);
				return result?.Content?.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? "";
			}
			catch (Exception ex)
			{
				_logger.LogDebug(ex, "MCP {Tool} failed (game server likely offline)", tool);
				return null;
			}
		}
	}
}
