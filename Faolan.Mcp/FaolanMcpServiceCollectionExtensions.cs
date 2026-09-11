using Faolan.Core.Cli;
using Faolan.Core.Network.Server.ChatServer;
using Faolan.Mcp.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Faolan.Mcp
{
	public static class FaolanMcpServiceCollectionExtensions
	{
		/// <summary>
		///     Registers the in-process MCP server (streamable HTTP) directly on Faolan's own container, so the tools
		///     resolve the same live singletons the server uses (
		///     <see cref="Faolan.Core.Database.Repositories.IDatabaseRepository" />,
		///     the chat/game dispatchers, <see cref="ClaudeChannel" />) with no nested host and no by-instance re-import.
		///     The host must be a <see cref="WebApplication" /> - call <see cref="MapFaolanMcp" /> on it to expose the endpoint.
		/// </summary>
		public static IServiceCollection AddFaolanMcp(this IServiceCollection services)
		{
			services
				.AddMcpServer()
				.WithHttpTransport()
				.WithTools<DatabaseTools>()
				.WithTools<SessionTools>()
				.WithTools<GameServerTools>()
				.WithTools<CharacterTools>()
				.WithTools<EconomyTools>()
				.WithTools<ClaudeChannelTools>();

			return services;
		}

		/// <summary>
		///     Maps the MCP endpoint at <c>/mcp</c> and binds Kestrel to the localhost MCP port for
		///     <paramref name="realmId" />. Localhost-only: the tools can drive the live server (GM commands, spawns),
		///     so the endpoint is never exposed off-box.
		///     Connect Claude with:  claude mcp add --transport http faolan http://127.0.0.1:&lt;port&gt;/mcp
		/// </summary>
		/// <remarks>
		///     The port is per realm. <c>Mcp:Port</c> is one global key and MCP runs only in a realm-hosting process, so
		///     without the offset every realm host would bind the same port and all but the first would die on "address
		///     already in use". Everything else about a realm host is per-realm already: the world, chat and CS ports
		///     come off the <c>Realm</c> row.
		/// </remarks>
		public static WebApplication MapFaolanMcp(this WebApplication app, uint realmId = FirstRealmId)
		{
			app.Urls.Add($"http://127.0.0.1:{McpPortFor(app.Configuration.McpPort, realmId)}");
			app.MapMcp("/mcp");
			return app;
		}

		/// <summary>The realm id the configured <c>Mcp:Port</c> is taken to mean, so its default keeps that port.</summary>
		public const uint FirstRealmId = 1;

		/// <summary>
		///     The MCP port a realm host binds: the configured base plus the realm's offset from the first realm.
		/// </summary>
		/// <remarks>
		///     The offset rather than the raw id is what keeps realm 1 on the configured port exactly, so a single-realm
		///     run still answers on the configured port and no <c>claude mcp add</c> has to change. Realm 2 then takes
		///     the next port, and so on.
		///     <para>
		///         Realm ids are not promised to be small or contiguous, so a far-off id would run past a ushort. The
		///         port is therefore clamped into the ephemeral-free range rather than wrapping silently; a deployment
		///         that hits the clamp should set <c>Mcp:Port</c> per process instead.
		///     </para>
		/// </remarks>
		public static int McpPortFor(ushort basePort, uint realmId)
		{
			var offset = realmId > FirstRealmId
				? realmId - FirstRealmId
				: 0;
			var port = basePort + (long)offset;

			return port is > 0 and <= ushort.MaxValue
				? (int)port
				: basePort;
		}
	}
}
