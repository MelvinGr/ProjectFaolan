using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Faolan.ChatServer;
using Faolan.Core;
using Faolan.Core.Cli;
using Faolan.Core.Database;
using Faolan.Core.Database.Extensions;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Extensions;
using Faolan.Core.Helpers;
using Faolan.Core.Network.Logging;
using Faolan.Core.Network.Server.ChatServer;
using Faolan.Core.Network.Server.GameServer;
using Faolan.CSPlayerAgent;
using Faolan.GameServer;
using Faolan.Helpers;
using Faolan.Mcp;
using Faolan.PlayerAgent;
using Faolan.UniverseAgent;
using Microsoft.AspNetCore.Builder;
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

			// Parse ONCE, up front - BEFORE the banner, so `--help` answers with help rather than nine lines of ASCII
			// art first. An unknown option is an error here rather than a setting that quietly stays on its default.
			// Null means the parser handled the invocation itself (--help, or it reported an error).
			var options = ServerCommand.Parse(args, out var parseExit);
			if (options == null)
			{
				Environment.ExitCode = parseExit;
				return;
			}

			Console.WriteLine(Global.StartupBanner);

			var (realmId, runLogin, runRealm, runMcp) = GetRunInfo(options);

			IHostApplicationBuilder builder = runMcp
				? WebApplication.CreateBuilder(args)
				: Host.CreateApplicationBuilder(args);

			if (Global.IsDebug)
				builder.Environment.EnvironmentName = "Development";

			// Sources are cleared first so the precedence is exactly env > cmdline > json (no appsettings.json in the mix).
			//builder.Configuration.Sources.Clear();
			AddConfigSources(builder.Configuration, options.ConfigOverrides);

			// Packet trace bytes: each packet logs on a single line; set Log:PacketBytes false (faolan.json "Log": {
			// "PacketBytes": false }, or --Log:PacketBytes false) to drop the raw headerData/body hex dump.
			PacketLogOptions.ShouldLogBytes = builder.Configuration.LogPacketBytes;

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

			builder.Logging.AddProvider(new FileLoggerProvider(builder.Configuration.LogFile));

			Realm? hostRealm = null;
			try
			{
				await using var db = DatabaseExtensions.CreateContext(builder.Configuration.DatabaseProvider, builder.Configuration.DatabaseConnection);

				// The shared migrate + load-the-data + seed sequence (DatabaseInitializer), logged Console-style because
				// the host - and with it any ILogger - does not exist yet. A database with no content gets faolan.sql
				// loaded into it here, which is what lets a clone run without the committed binary (#412).
				var seeded = await DatabaseInitializer.InitializeAsync(db);
				if (seeded.DataSeeded)
					Console.WriteLine($"[db] Empty database - loaded the game data from {Global.SeedPath}.");
				if (seeded.RealmCreated)
					Console.WriteLine("[db] No realm present - inserted the default realm.");
				if (seeded.AccountCreated)
					Console.WriteLine("[db] No account present - inserted the default account.");

				if (runRealm)
				{
					hostRealm = realmId.HasValue
						? db.Realms.FirstOrDefault(r => r.Id == realmId.Value)
						: db.Realms.OrderBy(r => r.Id).FirstOrDefault();
					// Only reachable for an explicit id - the no-id branch always finds a realm, since the seed above
					// inserts the default one when the table is empty.
					if (hostRealm == null)
						throw new Exception($"--role realm={realmId}: no such realm in the DB (add the Realm row, or omit the id to host the first one).");
				}
			}
			catch (Exception ex)
			{
				// A failed migration leaves the schema half-applied; refuse to start rather than limping on. Written to
				// stderr because the file logger isn't up yet - so a container/service run still captures the reason.
				Console.Error.WriteLine($"[db] FATAL: database bootstrap failed - refusing to start on a broken schema.\n{ex}");
				throw;
			}

			Console.WriteLine($"[role] login={runLogin} realm={runRealm}" + (hostRealm != null
				? $" — hosting '{hostRealm.Name}' (id {hostRealm.Id}): world {hostRealm.Port} / chat {hostRealm.ChatServerPort} / cs {hostRealm.CsPlayerAgentPort}"
				: ""));

			builder.Services.AddLogging();

			builder.Services.ConfigureFaolanDatabase(builder.Configuration);

			if (runLogin)
			{
				builder.Services.AddHostedService<UniverseAgentListener>();
				builder.Services.AddHostedService<PlayerAgentListener>();
			}

			if (runRealm)
			{
				// The realm-local listeners bind THIS realm's ports straight from its Realm row (single source of truth,
				// #324) - factory-registered so the port is injected, not read from global config.
				builder.Services.AddHostedService(provider => new CsPlayerAgentListener(hostRealm!.CsPlayerAgentPort, provider.GetRequiredService<ILogger<CsPlayerAgentListener>>(),
					provider.GetRequiredService<IConfiguration>(), provider.GetRequiredService<IDatabaseRepository>(), provider.GetRequiredService<IGameServerDispatcher>()));

				builder.Services.AddSingleton<IChatServerDispatcher, ChatServerDispatcher>();
				builder.Services.AddSingleton<ClaudeChannel>(); // in-game "Claude" chat channel log, bridged to Claude via the MCP chat tools
				builder.Services.AddHostedService(provider => new ChatServerListener(hostRealm!.ChatServerPort, provider.GetRequiredService<ILogger<ChatServerListener>>(),
					provider.GetRequiredService<IConfiguration>(), provider.GetRequiredService<IDatabaseRepository>(), provider.GetRequiredService<IChatServerDispatcher>(),
					provider.GetRequiredService<IGameServerDispatcher>(), provider.GetRequiredService<ClaudeChannel>()));

				// Live seam into the GameServer listener: aggregates world sessions and routes
				// GM commands. Mirrors the agent dispatcher; lets the MCP server spawn/teleport via validated GM paths.
				builder.Services.AddSingleton<IGameServerDispatcher, GameServerDispatcher>();

				// In-process MCP server (Claude tooling) mapped onto THIS host's Kestrel; shares the live singletons above
				// directly. runMcp already folds in McpEnabled and drove the WebApplication host-shape choice above, so
				// the AddFaolanMcp registration and the MapFaolanMcp endpoint (at the tail) are consistently gated on it.
				// When disabled the ChatServer also skips the Claude chat-channel subscription (nothing reads the log).
				if (runMcp)
					builder.Services.AddFaolanMcp();

				builder.Services.AddHostedService(provider =>
				{
					var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
					var configuration = provider.GetRequiredService<IConfiguration>();
					var dataRepository = provider.GetRequiredService<IDatabaseRepository>();
					var agentServerDispatcher = provider.GetRequiredService<IChatServerDispatcher>();
					var gameServerDispatcher = provider.GetRequiredService<IGameServerDispatcher>();

					return new GameServerListener(loggerFactory.CreateLogger($"{typeof(GameServerListener).FullName}-{hostRealm!.Id}"), configuration, dataRepository, hostRealm!,
						agentServerDispatcher, gameServerDispatcher);
				});

				// Registered LAST so it starts after the listeners (Online = the host is up) and, since hosted services
				// stop in reverse order, flips the realm Offline first as shutdown begins (#324).
				builder.Services.AddHostedService(provider =>
					new RealmStatusService(provider.GetRequiredService<IRealmRepository>(), provider.GetRequiredService<ILogger<RealmStatusService>>(), hostRealm!.Id));
			}

			// One host. When MCP runs, the builder is a WebApplication - map the endpoint onto its Kestrel and run it;
			// otherwise it's a plain generic host with no web server (see runMcp above).
			if (builder is WebApplicationBuilder webBuilder)
			{
				var app = webBuilder.Build();
				app.MapFaolanMcp();
				await app.RunAsync();
			}
			else
			{
				var app = ((HostApplicationBuilder)builder).Build();
				await app.RunAsync();
			}
		}

		// Config sources (highest precedence wins): environment variables > command line > faolan.json, in the shared
		// order every Faolan executable uses (FaolanCommandLine). faolan.json is OPTIONAL - a container can be
		// configured entirely by env vars with no file baked in. The command-line half arrives ALREADY PARSED, so the
		// same values shape the throwaway config below and the host builder's own.
		private static void AddConfigSources(IConfigurationBuilder cfg, Dictionary<string, string?> commandLine)
		{
			cfg.AddConfiguration(FaolanCommandLine.Build(commandLine));
		}

		private static (uint? realmId, bool runLogin, bool runRealm, bool runMcp) GetRunInfo(ServerOptions options)
		{
			// Process ROLE (multi-realm / process-per-realm, #324). --role is REPEATABLE, so one process can compose
			// roles (e.g. `--role login --role realm=2` = login + realm 2 in one process):
			//   --role login       -> the shared login/router tier (UniverseAgent + PlayerAgent); owns no world.
			//   --role realm=<id>  -> a game host for ONE realm (GameServer + realm-local chat + guilds), bound to that
			//                         realm's ports; the login tier routes players to it (SendGameServer/ChatServer/CsPlayerAgent).
			//   --role realm       -> the same, for the first realm (id omitted).
			//   (no --role)        -> all-in-one: login + a game host for the first realm (the historical dev run).
			// At most ONE realm role per process: co-hosting several realms in one process would share the chat/world
			// dispatchers + session aggregation across realms (a cross-realm leak) - that's what separate processes avoid.
			// Parsed from argv up front (before the host is built) because the host SHAPE depends on it - see runMcp below.

			// The option is REPEATABLE, which the parser handles (ServerCommand) - a configuration binder could not,
			// since it keeps only the LAST value of a repeated key.
			var roles = options.Roles;

			uint? selectedRealmId = null;
			bool runLogin = false, runRealm = false;
			if (roles.Length == 0)
				runLogin = runRealm = true; // default: all-in-one (dev)
			else
			{
				foreach (var role in roles)
				{
					if (role.Equals("login", StringComparison.OrdinalIgnoreCase))
						runLogin = true;
					else if (role.Equals("realm", StringComparison.OrdinalIgnoreCase) || role.StartsWith("realm=", StringComparison.OrdinalIgnoreCase))
					{
						if (runRealm)
							throw new Exception("At most one 'realm' role per process!");

						runRealm = true;
						var eq = role.IndexOf('=');
						if (eq >= 0)
							selectedRealmId = uint.Parse(role[(eq + 1)..], CultureInfo.InvariantCulture);
					}
					else
						throw new Exception($"--role must be 'login' or 'realm[=<id>]', got '{role}'.");
				}
			}

			// The in-process MCP server (streamable HTTP on a localhost port) is the ONLY thing here that needs a web
			// server. It runs only for a realm host and only when enabled, so we pick the host shape accordingly: a
			// WebApplication (Kestrel) when MCP runs - mapping the endpoint straight onto that host, sharing its DI so the
			// tools resolve the live singletons directly (no nested host, no by-instance re-import) - and a plain generic
			// host otherwise, so a login-only / MCP-off process stays web-server-free (no stray bound port). McpEnabled is
			// read from a throwaway config; the builder's own configuration is set up identically just below.
			var preConfig = new ConfigurationBuilder();
			AddConfigSources(preConfig, options.ConfigOverrides);
			var runMcp = runRealm && preConfig.Build().McpEnabled;
			return (selectedRealmId, runLogin, runRealm, runMcp);
		}
	}
}
