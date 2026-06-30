using System;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Extensions;
using Faolan.Core.Network.AgentServer;
using Faolan.Core.Network.GameServer;
using Faolan.Mcp.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Faolan.Mcp
{
	/// <summary>
	///     Hosts an MCP server (streamable HTTP) on a localhost port from inside the live Faolan process.
	///     It runs a nested ASP.NET Core / Kestrel app whose DI shares Faolan's live singletons by instance,
	///     so MCP tools observe the exact same <see cref="IDatabaseRepository" /> and
	///     <see cref="IAgentServerDispatcher" /> the running server uses — that's what makes the tools able to
	///     inspect and (later) drive the live server rather than just the database file.
	///     Connect Claude with:  claude mcp add --transport http faolan http://127.0.0.1:&lt;McpPort&gt;/mcp
	/// </summary>
	public sealed class FaolanMcpHostedService : IHostedService
	{
		private readonly IConfiguration _configuration;
		private readonly ILogger<FaolanMcpHostedService> _logger;
		private readonly ILoggerFactory _loggerFactory;
		private readonly IServiceProvider _parentServices;
		private WebApplication? _app;

		public FaolanMcpHostedService(IServiceProvider parentServices, IConfiguration configuration, ILoggerFactory loggerFactory, ILogger<FaolanMcpHostedService> logger)
		{
			_parentServices = parentServices;
			_configuration = configuration;
			_loggerFactory = loggerFactory;
			_logger = logger;
		}

		public async Task StartAsync(CancellationToken cancellationToken)
		{
			var port = _configuration.McpPort;

			var builder = WebApplication.CreateSlimBuilder();

			// The nested host should not stand up its own console logger; route everything through the
			// parent pipeline so MCP traffic shares Faolan's existing log sinks.
			builder.Logging.ClearProviders();
			builder.Services.AddSingleton(_loggerFactory);

			// Share the parent process's live singletons *by instance*. Both are registered as singletons in
			// Faolan's root container, so the tools resolve the same objects the server is actively using.
			builder.Services.AddSingleton(_parentServices.GetRequiredService<IDatabaseRepository>());
			builder.Services.AddSingleton(_parentServices.GetRequiredService<IAgentServerDispatcher>());
			builder.Services.AddSingleton(_parentServices.GetRequiredService<IGameServerDispatcher>());
			builder.Services.AddSingleton(_parentServices.GetRequiredService<ClaudeChannel>());

			builder
				.Services
				.AddMcpServer()
				.WithHttpTransport()
				.WithTools<DatabaseTools>()
				.WithTools<SessionTools>()
				.WithTools<GameServerTools>()
				.WithTools<CharacterTools>()
				.WithTools<EconomyTools>()
				.WithTools<ClaudeChannelTools>();

			_app = builder.Build();
			_app.Urls.Add($"http://127.0.0.1:{port}");
			_app.MapMcp("/mcp");

			await _app.StartAsync(cancellationToken);
			_logger.LogInformation("Faolan MCP server listening on http://127.0.0.1:{Port}/mcp", port);
		}

		public async Task StopAsync(CancellationToken cancellationToken)
		{
			if (_app == null)
				return;

			await _app.StopAsync(cancellationToken);
			await _app.DisposeAsync();
			_app = null;
		}
	}
}
