using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Faolan.Web.Services
{
	// One live connected player, as reported by the game server's MCP `list_sessions` tool. Mirrors
	// Faolan.Core's SessionInfo record (we deserialize the tool's JSON rather than referencing the server types).
	public sealed record LiveSession(
		uint? CharacterId, string? CharacterName, ushort? CharacterLevel, string? AccountName);

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
			try
			{
				var transport = new HttpClientTransport(new HttpClientTransportOptions
				{
					Endpoint = _endpoint,
					Name = "faolan-web"
				});
				await using var client = await McpClient.CreateAsync(transport, cancellationToken: ct);

				var result = await client.CallToolAsync("list_sessions", cancellationToken: ct);
				var text = result?.Content?.OfType<TextContentBlock>().FirstOrDefault()?.Text;

				// The tool returns a human string when empty, else a JSON array of SessionInfo.
				if (string.IsNullOrWhiteSpace(text) || !text.TrimStart().StartsWith('['))
					return [];

				return JsonSerializer.Deserialize<List<LiveSession>>(text, JsonOptions) ?? [];
			}
			catch (Exception ex)
			{
				_logger.LogDebug(ex, "MCP list_sessions failed (game server likely offline)");
				return null;
			}
		}
	}
}
