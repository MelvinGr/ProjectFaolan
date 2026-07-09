using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Faolan.AgentServer;
using Faolan.Core;
using Faolan.Core.Database.Extensions;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Extensions;
using Faolan.Core.Helpers;
using Faolan.Core.Network.Server.AgentServer;
using Faolan.Core.Network.Server.GameServer;
using Faolan.CSPlayerAgent;
using Faolan.GameServer;
using Faolan.Helpers;
using Faolan.Mcp;
using Faolan.PlayerAgent;
using Faolan.UniverseAgent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Faolan
{
	public static class Program
	{
		public static byte[] LoadEmbeddedResource(string name)
		{
			using var stream = typeof(Program).Assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"embedded resource '{name}' not found");
			using var buffer = new MemoryStream();
			stream.CopyTo(buffer);
			return buffer.ToArray();
		}

		[STAThread]
		public static async Task Main(string[] args)
		{
			Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
			Console.WriteLine(Global.StartupBanner);

			var builder = Host.CreateApplicationBuilder(args);

			if (Global.IsDebug)
				builder.Environment.EnvironmentName = "Development";

			builder.Configuration.SetBasePath(Directory.GetCurrentDirectory());
			builder.Configuration.AddJsonFile(Path.GetFullPath("faolan.json"));
			builder.Configuration.AddCommandLine(args);

			builder.Logging.SetMinimumLevel(Global.IsDebug
				? LogLevel.Debug
				: LogLevel.Information);
			builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);

			builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
			builder.Logging.AddFilter("ModelContextProtocol.Server.McpServer", LogLevel.Warning);

			builder.Logging.AddSimpleConsole(options =>
			{
				options.IncludeScopes = true;
				options.SingleLine = true;
			});

			// Mirror the console log to a file. FAOLAN_LOG_FILE overrides the location (e.g. a mounted volume in a
			// container); default is logs/<epoch>.log (epoch = process start, unix seconds) so each run is a
			// distinct, retained file instead of overwriting one logs.txt every start (#282).
			builder.Logging.AddProvider(new FileLoggerProvider(Environment.GetEnvironmentVariable("FAOLAN_LOG_FILE") ??
															   Path.GetFullPath(Path.Combine("logs", $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.log"))));

			builder.Services.AddLogging();

			builder.Services.ConfigureFaolanDatabase(builder.Configuration);
			builder.Services.AddScopedHostedService<DatabaseMigrator>();

			builder.Services.AddScopedHostedService<UniverseAgentListener>();
			builder.Services.AddScopedHostedService<PlayerAgentListener>();
			builder.Services.AddScopedHostedService<CsPlayerAgentListener>();

			builder.Services.AddSingleton<IAgentServerDispatcher, AgentServerDispatcher>();
			builder.Services.AddSingleton<ClaudeChannel>(); // in-game "Claude" chat channel log, bridged to Claude via the MCP chat tools
			builder.Services.AddScopedHostedService<AgentServerListener>();

			// Live seam into the GameServer listener: aggregates world sessions and routes
			// GM commands. Mirrors the agent dispatcher; lets the MCP server spawn/teleport via validated GM paths.
			builder.Services.AddSingleton<IGameServerDispatcher, GameServerDispatcher>();

			// In-process MCP server (Claude tooling) on a localhost Kestrel port; shares the live singletons above.
			// When disabled the AgentServer also skips the Claude chat-channel subscription (nothing reads the log).
			if (builder.Configuration.McpEnabled)
				builder.Services.AddFaolanMcp();

			builder.Services.AddScopedHostedService(provider =>
			{
				var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
				var configuration = provider.GetRequiredService<IConfiguration>();
				var dataRepository = provider.GetRequiredService<IDatabaseRepository>();
				var agentServerDispatcher = provider.GetRequiredService<IAgentServerDispatcher>();
				var gameServerDispatcher = provider.GetRequiredService<IGameServerDispatcher>();

				// One-time startup resolution of the listener's realm/port; blocking here is fine (host build).
				var realm = dataRepository.GetRealms().GetAwaiter().GetResult().FirstOrDefault();
				if (realm == null)
					throw new Exception("realm == null");

				return new GameServerListener(loggerFactory.CreateLogger($"{typeof(GameServerListener).FullName}-{realm.Id}"), configuration, dataRepository, realm.Port, agentServerDispatcher,
					gameServerDispatcher);
			});

			var host = builder.Build();
			await host.RunAsync();
		}
	}
}
