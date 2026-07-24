using Faolan.Core.Extensions;
using Faolan.Mcp.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
		///     Maps the MCP endpoint at <c>/mcp</c> and binds Kestrel to the localhost MCP port. Localhost-only: the tools
		///     can drive the live server (GM commands, spawns), so the endpoint is never exposed off-box.
		///     Connect Claude with:  claude mcp add --transport http faolan http://127.0.0.1:&lt;McpPort&gt;/mcp
		/// </summary>
		public static WebApplication MapFaolanMcp(this WebApplication app)
		{
			var port = app.Configuration.McpPort;
			app.Urls.Add($"http://127.0.0.1:{port}");
			app.MapMcp("/mcp");
			app.Logger.LogInformation("Faolan MCP server listening on http://127.0.0.1:{Port}/mcp", port);
			return app;
		}
	}
}
