using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Faolan.AgentServer;
using Faolan.Core;
using Faolan.Core.Database;
using Faolan.Core.Database.Extensions;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Microsoft.EntityFrameworkCore;
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

			// Process ROLE (multi-realm / process-per-realm, #324) via a single --role option:
			//   --role login       -> the shared login/router tier (UniverseAgent + PlayerAgent); owns no world.
			//   --role realm=<id>  -> a game host for ONE realm (GameServer + realm-local chat + guilds), bound to that
			//                         realm's ports; the login tier routes players to it (SendGameServer/AgentServer/CsPlayerAgent).
			//   --role realm       -> the same, for the first realm (id omitted).
			//   (no --role)        -> all-in-one: login + a game host for the first realm (the historical single-process dev run).
			var roleArg = builder.Configuration["role"];
			uint? selectedRealmId = null;
			bool runLogin, runRealm;
			if (string.IsNullOrWhiteSpace(roleArg))
			{
				runLogin = runRealm = true; // default: all-in-one (dev)
			}
			else if (roleArg.Equals("login", StringComparison.OrdinalIgnoreCase))
			{
				runLogin = true;
				runRealm = false;
			}
			else if (roleArg.Equals("realm", StringComparison.OrdinalIgnoreCase) || roleArg.StartsWith("realm=", StringComparison.OrdinalIgnoreCase))
			{
				runRealm = true;
				runLogin = false;
				var eq = roleArg.IndexOf('=');
				if (eq >= 0)
					selectedRealmId = uint.Parse(roleArg[(eq + 1)..], CultureInfo.InvariantCulture);
			}
			else
			{
				throw new Exception($"--role must be 'login' or 'realm[=<id>]', got '{roleArg}'.");
			}

			// The realm this process hosts (realm role). Resolved from the DB up front - the Realm row is the single
			// source of truth for the bind ports, so the world/chat/guild listeners bind exactly what the login tier
			// advertises for this realm (SendGameServer/AgentServer/CsPlayerAgent). Needs a migrated + seeded DB.
			Realm hostRealm = null;
			if (runRealm)
			{
				using var probe = new DatabaseContext(new DbContextOptionsBuilder<DatabaseContext>().UseSqlite($"Data Source={builder.Configuration.DatabasePath}").Options);
				hostRealm = selectedRealmId.HasValue
					? probe.Realms.FirstOrDefault(r => r.Id == selectedRealmId.Value)
					: probe.Realms.OrderBy(r => r.Id).FirstOrDefault();
				if (hostRealm == null)
					throw new Exception(selectedRealmId.HasValue
						? $"--role realm={selectedRealmId}: no such realm in the DB (migrate + seed a Realm row first)."
						: "--role realm: no realm seeded in the DB.");

				// Bind the realm-local chat/guild listeners (they read config) to THIS realm's ports.
				builder.Configuration.AddInMemoryCollection(new Dictionary<string, string>
				{
					["AgentServerPort"] = hostRealm.AgentServerPort.ToString(CultureInfo.InvariantCulture),
					["CsPlayerAgentPort"] = hostRealm.CsPlayerAgentPort.ToString(CultureInfo.InvariantCulture)
				});
			}

			Console.WriteLine($"[role] login={runLogin} realm={runRealm}" +
							  (hostRealm != null ? $" — hosting '{hostRealm.Name}' (id {hostRealm.Id}): world {hostRealm.Port} / chat {hostRealm.AgentServerPort} / cs {hostRealm.CsPlayerAgentPort}" : ""));

			builder.Services.AddLogging();

			builder.Services.ConfigureFaolanDatabase(builder.Configuration);
			builder.Services.AddScopedHostedService<DatabaseMigrator>();

			if (runLogin)
			{
				builder.Services.AddScopedHostedService<UniverseAgentListener>();
				builder.Services.AddScopedHostedService<PlayerAgentListener>();
			}

			if (runRealm)
			{
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

				var hostRealmId = hostRealm.Id;
				var hostRealmPort = hostRealm.Port;
				builder.Services.AddScopedHostedService(provider =>
				{
					var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
					var configuration = provider.GetRequiredService<IConfiguration>();
					var dataRepository = provider.GetRequiredService<IDatabaseRepository>();
					var agentServerDispatcher = provider.GetRequiredService<IAgentServerDispatcher>();
					var gameServerDispatcher = provider.GetRequiredService<IGameServerDispatcher>();

					return new GameServerListener(loggerFactory.CreateLogger($"{typeof(GameServerListener).FullName}-{hostRealmId}"), configuration, dataRepository, hostRealmPort, agentServerDispatcher,
						gameServerDispatcher);
				});
			}

			var host = builder.Build();
			await host.RunAsync();
		}
	}
}
