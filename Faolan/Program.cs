using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Faolan.ChatServer;
using Faolan.Core;
using Faolan.Core.Database;
using Faolan.Core.Database.Extensions;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Extensions;
using Faolan.Core.Helpers;
using Faolan.Core.Network;
using Faolan.Core.Network.Server.ChatServer;
using Faolan.Core.Network.Server.GameServer;
using Faolan.CSPlayerAgent;
using Faolan.GameServer;
using Faolan.Helpers;
using Faolan.Mcp;
using Faolan.PlayerAgent;
using Faolan.UniverseAgent;
using Microsoft.EntityFrameworkCore;
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

		// All --role values from argv (the option is repeatable: `--role login --role realm=2`). The config binder keeps
		// only the LAST value for a repeated key, so the roles are parsed straight from argv. Handles `--role X` and `--role=X`.
		private static List<string> CollectRoleArgs(string[] args)
		{
			var roles = new List<string>();
			for (var i = 0; i < args.Length; i++)
			{
				if (args[i].Equals("--role", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
					roles.Add(args[++i]);
				else if (args[i].StartsWith("--role=", StringComparison.OrdinalIgnoreCase))
					roles.Add(args[i]["--role=".Length..]);
			}

			return roles;
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

			// Packet trace bytes: each packet logs on a single line; set "LogPacketBytes": false (faolan.json or
			// --LogPacketBytes false) to drop the raw headerData/body hex dump and keep just the opcode + lengths.
			PacketLogOptions.IncludeBytes = builder.Configuration.GetValue("LogPacketBytes", true);

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

			// Process ROLE (multi-realm / process-per-realm, #324). --role is REPEATABLE, so one process can compose
			// roles (e.g. `--role login --role realm=2` = login + realm 2 in one process):
			//   --role login       -> the shared login/router tier (UniverseAgent + PlayerAgent); owns no world.
			//   --role realm=<id>  -> a game host for ONE realm (GameServer + realm-local chat + guilds), bound to that
			//                         realm's ports; the login tier routes players to it (SendGameServer/ChatServer/CsPlayerAgent).
			//   --role realm       -> the same, for the first realm (id omitted).
			//   (no --role)        -> all-in-one: login + a game host for the first realm (the historical dev run).
			// At most ONE realm role per process: co-hosting several realms in one process would share the chat/world
			// dispatchers + session aggregation across realms (a cross-realm leak) - that's what separate processes avoid.
			var roles = CollectRoleArgs(args); // repeated --role values, parsed from argv (config binding keeps only the last)
			uint? selectedRealmId = null;
			bool runLogin = false, runRealm = false;
			if (roles.Count == 0)
			{
				runLogin = runRealm = true; // default: all-in-one (dev)
			}
			else
			{
				foreach (var role in roles)
				{
					if (role.Equals("login", StringComparison.OrdinalIgnoreCase))
					{
						runLogin = true;
					}
					else if (role.Equals("realm", StringComparison.OrdinalIgnoreCase) || role.StartsWith("realm=", StringComparison.OrdinalIgnoreCase))
					{
						if (runRealm)
							throw new Exception("at most one 'realm' role per process - co-hosting realms would leak chat/session state across them; run one process per realm.");
						runRealm = true;
						var eq = role.IndexOf('=');
						if (eq >= 0)
							selectedRealmId = uint.Parse(role[(eq + 1)..], CultureInfo.InvariantCulture);
					}
					else
					{
						throw new Exception($"--role must be 'login' or 'realm[=<id>]', got '{role}'.");
					}
				}
			}

			// The realm this process hosts (realm role). Resolved from the DB up front - the Realm row is the single
			// source of truth for the bind ports, so the world/chat/guild listeners bind exactly what the login tier
			// advertises for this realm (SendGameServer/ChatServer/CsPlayerAgent). Needs a migrated + seeded DB.
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
			}

			Console.WriteLine($"[role] login={runLogin} realm={runRealm}" +
							  (hostRealm != null ? $" — hosting '{hostRealm.Name}' (id {hostRealm.Id}): world {hostRealm.Port} / chat {hostRealm.ChatServerPort} / cs {hostRealm.CsPlayerAgentPort}" : ""));

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
				// The realm-local listeners bind THIS realm's ports straight from its Realm row (single source of truth,
				// #324) - factory-registered so the port is injected, not read from global config.
				builder.Services.AddScopedHostedService(provider => new CsPlayerAgentListener(hostRealm.CsPlayerAgentPort, provider.GetRequiredService<ILogger<CsPlayerAgentListener>>(),
					provider.GetRequiredService<IConfiguration>(), provider.GetRequiredService<IDatabaseRepository>(), provider.GetRequiredService<IGameServerDispatcher>()));

				builder.Services.AddSingleton<IChatServerDispatcher, ChatServerDispatcher>();
				builder.Services.AddSingleton<ClaudeChannel>(); // in-game "Claude" chat channel log, bridged to Claude via the MCP chat tools
				builder.Services.AddScopedHostedService(provider => new ChatServerListener(hostRealm.ChatServerPort, provider.GetRequiredService<ILogger<ChatServerListener>>(),
					provider.GetRequiredService<IConfiguration>(), provider.GetRequiredService<IDatabaseRepository>(), provider.GetRequiredService<IChatServerDispatcher>(),
					provider.GetRequiredService<IGameServerDispatcher>(), provider.GetRequiredService<ClaudeChannel>()));

				// Live seam into the GameServer listener: aggregates world sessions and routes
				// GM commands. Mirrors the agent dispatcher; lets the MCP server spawn/teleport via validated GM paths.
				builder.Services.AddSingleton<IGameServerDispatcher, GameServerDispatcher>();

				// In-process MCP server (Claude tooling) on a localhost Kestrel port; shares the live singletons above.
				// When disabled the ChatServer also skips the Claude chat-channel subscription (nothing reads the log).
				if (builder.Configuration.McpEnabled)
					builder.Services.AddFaolanMcp();

				builder.Services.AddScopedHostedService(provider =>
				{
					var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
					var configuration = provider.GetRequiredService<IConfiguration>();
					var dataRepository = provider.GetRequiredService<IDatabaseRepository>();
					var agentServerDispatcher = provider.GetRequiredService<IChatServerDispatcher>();
					var gameServerDispatcher = provider.GetRequiredService<IGameServerDispatcher>();

					return new GameServerListener(loggerFactory.CreateLogger($"{typeof(GameServerListener).FullName}-{hostRealm.Id}"),
						configuration, dataRepository, hostRealm, agentServerDispatcher, gameServerDispatcher);
				});

				// Registered LAST so it starts after the listeners (Online = the host is up) and, since hosted services
				// stop in reverse order, flips the realm Offline first as shutdown begins (#324).
				builder.Services.AddScopedHostedService(provider =>
					new RealmStatusService(provider.GetRequiredService<IDatabaseRepository>(), provider.GetRequiredService<ILogger<RealmStatusService>>(), hostRealm.Id));
			}

			var host = builder.Build();
			await host.RunAsync();
		}
	}
}
