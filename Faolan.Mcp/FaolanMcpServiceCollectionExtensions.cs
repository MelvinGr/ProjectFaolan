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
		///     resolve the SAME live singletons the server uses (
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
		///     ⚠️ The port is PER REALM (#910). `Mcp:Port` is one global key, and MCP runs only in a realm-hosting
		///     process, so every realm host bound the same 7099 and the second one died on `address already in use` -
		///     one Faolan process per machine could have MCP on, which is exactly the developer testing multi-realm.
		///     Everything else about a realm host is already per-realm (the world/chat/cs ports come off the `Realm`
		///     row); this was the one thing that was not.
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
		///     The MCP port a realm host binds: the configured base plus the realm's OFFSET from the first realm.
		/// </summary>
		/// <remarks>
		///     The offset rather than the raw id is what keeps realm 1 on the configured port exactly - so the single-realm
		///     dev run, which is every existing setup, still answers on 7099 and no `claude mcp add` has to change.
		///     Realm 2 then takes 7100, and so on.
		///     <para>
		///         ⚠️ Realm ids are not promised to be small or contiguous, so a far-off id would run past a ushort. The
		///         port is therefore CLAMPED into the ephemeral-free range rather than wrapping silently; a deployment
		///         that hits the clamp should set <c>Mcp:Port</c> per process instead, which has always worked.
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
