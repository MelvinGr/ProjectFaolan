using System;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Faolan.ChatServer;
using Faolan.Core;
using Faolan.Core.Cli;
using Faolan.Core.Database;
using Faolan.Core.Database.Extensions;
using Faolan.Core.Database.Tables;
using Faolan.Core.Helpers;
using Faolan.Core.Network.Logging;
using Faolan.Core.Network.Server.ChatServer;
using Faolan.Core.Network.Server.CsPlayerAgent;
using Faolan.Core.Network.Server.GameServer;
using Faolan.CSPlayerAgent;
using Faolan.GameServer;
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

			// The process ROLE set is parsed and validated by the command line itself (ServerCommand.TryParseRoles), so
			// an impossible combination has already been reported as a CLI error and this can only see a legal one.
			var (runLogin, runRealm, realmId) = (options.RunLogin, options.RunRealm, options.RealmId);

			// The in-process MCP server (streamable HTTP on a localhost port) is the ONLY thing here that needs a web
			// server. It runs only for a realm host and only when enabled, so we pick the host shape accordingly: a
			// WebApplication (Kestrel) when MCP runs - mapping the endpoint straight onto that host, sharing its DI so
			// the tools resolve the live singletons directly (no nested host, no by-instance re-import) - and a plain
			// generic host otherwise, so a login-only / MCP-off process stays web-server-free (no stray bound port).
			var runMcp = runRealm && options.Configuration.McpEnabled;

			IHostApplicationBuilder builder = runMcp
				? WebApplication.CreateBuilder(args)
				: Host.CreateApplicationBuilder(args);

			if (Global.IsDebug)
				builder.Environment.EnvironmentName = "Development";

			builder.Configuration.Sources.Clear();
			builder.Configuration.AddConfiguration(options.Configuration);

			// Packet trace bytes: each packet logs on a single line; set Log:PacketBytes false (faolan.json "Log": {
			// "PacketBytes": false }, or --Log:PacketBytes false) to drop the raw headerData/body hex dump.
			PacketLogOptions.ShouldLogBytes = builder.Configuration.LogPacketBytes;

			builder.Logging.SetMinimumLevel(Global.IsDebug
				? LogLevel.Debug
				: LogLevel.Information);

			builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
			builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
			builder.Logging.AddFilter("ModelContextProtocol.Server.McpServer", LogLevel.Warning);

			builder.Logging.AddSimpleConsole(o =>
			{
				o.IncludeScopes = true;
				o.SingleLine = true;
			});

			// Registered through a FACTORY, not as a pre-built instance: MS.DI disposes the singletons it creates and
			// leaves the ones it is handed alone, and `AddProvider(instance)` is the latter. The provider buffers its
			// writes and flushes on a 1 s timer (see FileLoggerProvider), so an undisposed one could lose the last
			// second of faolan.log at shutdown; owned by the container, its Dispose flushes when the host stops.
			var logFile = builder.Configuration.LogFile;
			builder.Logging.Services.AddSingleton<ILoggerProvider>(_ => new FileLoggerProvider(logFile));

			// The login DH private key (Server:PrivateKey). Read BEFORE any listener starts so a bad value throws here
			// rather than mid-login, where it would silently degrade every login to accept-any.
			var serverPrivateKey = builder.Configuration.ServerPrivateKey;

			// BOTH states get a line, because the one that matters most is the one that used to print NOTHING: a server
			// that checks no passwords looked identical at startup to one that does. "Unset" is no longer a built-in
			// key either - there is no key at all - so say what that means for logins rather than for config.
			if (serverPrivateKey == null)
			{
				Console.WriteLine(
					"[login] No Server:PrivateKey - passwords are NOT checked; every client is accepted on its account name alone. Run --generate-login-key and patch clients (Faolan.ClientLauncher --patch-login) to enable password validation.");
			}
			else
			{
				Console.WriteLine("[login] Using the configured Server:PrivateKey - every client must be patched (Faolan.ClientLauncher --patch-login) and its password is validated.");
			}

			// ⚠️ This block runs BEFORE the host on purpose, and the alternative was weighed and dropped (#409). Every
			// listener builds its `TcpListener` in its constructor, and the host constructs all hosted services before
			// starting any, so a port cannot be resolved from inside one. Deferring socket creation to StartAsync would
			// remove this block - at the cost of touching ServerBase and all five listeners, making `LocalEndPoint`
			// invalid until start (the bind log lines read it), moving the `[role]` banner into a service, and turning
			// two fail-fast errors - a bad `--role realm=<id>` and a port conflict - into failures during host start
			// with worse messages. It is the same work either way and this shape says it in one place, so it stays.
			Realm? hostRealm = null;
			try
			{
				await using var db = DatabaseExtensions.CreateContext(builder.Configuration.DatabaseProvider, builder.Configuration.DatabaseConnectionString);

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
				await Console.Error.WriteLineAsync($"[db] FATAL: database bootstrap failed - refusing to start on a broken schema.\n{ex}");
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
				// The realm THIS process hosts, as an immutable snapshot of the Realm row resolved above (single source
				// of truth for the realm-local service ports, #324). Registering it is what lets every realm-tier
				// service below be a plain AddHostedService<T>(): the port arrives by INJECTION rather than through a
				// factory lambda that reaches into the closed-over `hostRealm`.
				var realm = new HostRealm(hostRealm!.Id, hostRealm.Name, hostRealm.Port, hostRealm.ChatServerPort, hostRealm.CsPlayerAgentPort);
				builder.Services.AddSingleton(realm);

				// ⚠️ Registration ORDER is load-bearing and the four AddHostedService lines below must keep it:
				// HostOptions.ServicesStartConcurrently is false, so StartAsync is awaited in registration order. That
				// is what starts RealmStatusService last (Online = the host is up) and, since hosted services stop in
				// REVERSE order, flips the realm Offline first as shutdown begins (#324).

				// Seam into the CS listener: it owns the team roster, and LeaveTeam/KickFromTeam arrive on the world
				// connection, so the world listener routes them through here (as it does chat and world actions).
				builder.Services.AddSingleton<ICsPlayerAgentDispatcher, CsPlayerAgentDispatcher>();
				builder.Services.AddRealmCsPlayerAgent(); // its services, container-registered like the GameServer's
				builder.Services.AddHostedService<CsPlayerAgentListener>();

				builder.Services.AddSingleton<IChatServerDispatcher, ChatServerDispatcher>();
				builder.Services.AddHostedService<ChatServerListener>();

				// Live seam into the GameServer listener: aggregates world sessions and routes
				// GM commands. Mirrors the agent dispatcher; lets the MCP server spawn/teleport via validated GM paths.
				builder.Services.AddSingleton<IGameServerDispatcher, GameServerDispatcher>();

				// In-process MCP server (Claude tooling) mapped onto THIS host's Kestrel; shares the live singletons above
				// directly. runMcp already folds in McpEnabled and drove the WebApplication host-shape choice above, so
				// the AddFaolanMcp registration and the MapFaolanMcp endpoint (at the tail) are consistently gated on it.
				// When disabled the ChatServer also skips the Claude chat-channel subscription (nothing reads the log).
				if (runMcp)
					builder.Services.AddFaolanMcp();

				// The realm's ~60 world services (#826). They used to be hand-`new`ed in the GameServer listener's
				// ctor; the registration lists them in dependency order, which is documentation rather than a
				// requirement - MS.DI resolves by type. It also registers the bare realm-tagged `ILogger` every one
				// of them takes, which is why it is scoped to a process that actually hosts a realm.
				builder.Services.AddRealmGameServer(realm);

				// Its realm-tagged log category ("...GameServerListener-<realmId>") is built inside the listener from the
				// injected HostRealm - see its ctor for why that is an ILoggerFactory rather than an ILogger.
				builder.Services.AddHostedService<GameServerListener>();

				// Registered LAST so it starts after the listeners (Online = the host is up) and, since hosted services
				// stop in reverse order, flips the realm Offline first as shutdown begins (#324).
				builder.Services.AddHostedService<RealmStatusService>();
			}

			// One host. When MCP runs, the builder is a WebApplication - map the endpoint onto its Kestrel and run it;
			// otherwise it's a plain generic host with no web server (see runMcp above).
			if (builder is WebApplicationBuilder webBuilder)
			{
				var app = webBuilder.Build();

				// The MCP port is per-realm (#910): `Mcp:Port` is one global key and MCP only runs in a realm host, so
				// without the offset every realm process bound the same port and the second one died on
				// `address already in use` - naming a port the operator never set for it.
				var mcpPort = FaolanMcpServiceCollectionExtensions.McpPortFor(builder.Configuration.McpPort, hostRealm!.Id);
				app.MapFaolanMcp(hostRealm.Id);
				Console.WriteLine($"[mcp] http://127.0.0.1:{mcpPort}/mcp (realm {hostRealm.Id})");

				try
				{
					await app.RunAsync();
				}
				catch (IOException ex) when (ex.Message.Contains($":{mcpPort}", StringComparison.Ordinal))
				{
					// Kestrel's bind error names a port nobody configured for THIS process and says nothing about MCP,
					// which is what made the original failure opaque. Say what it is and how to get past it.
					await Console.Error.WriteLineAsync(
						$"[mcp] FATAL: could not bind the MCP endpoint on port {mcpPort}. Something else holds it - "
						+ $"another Faolan process hosting realm {hostRealm.Id}, or an unrelated program. "
						+ "Start this process with --Mcp:Enabled false, or move it with --Mcp:Port <port>.");
					throw;
				}
			}
			else
			{
				var app = ((HostApplicationBuilder)builder).Build();
				await app.RunAsync();
			}
		}
	}
}
