using System.Collections.Generic;
using System.CommandLine;
using System.Linq;
using Faolan.Core.Cli;

namespace Faolan
{
	// The server's command line. One real flag - `--role` - plus the configuration keys, declared so `--help` lists
	// them and a misspelling is an error rather than a setting that silently stays on its default.
	//
	// `--role` is REPEATABLE (`--role login --role realm=2` co-hosts both in one process), which the parser handles
	// natively; it used to be a hand-rolled scan over argv precisely because the configuration binder keeps only the
	// LAST value of a repeated key.
	public static class ServerCommand
	{
		private static readonly Option<string[]> Role = new("--role")
		{
			Description = "login | realm | realm=<id>. Repeatable: `--role login --role realm=2`. Omitted = all-in-one (login + the first realm).",
			AllowMultipleArgumentsPerToken = false
		};

		// Configuration keys - the option name IS the config path, so it is spelled the same here, in faolan.json
		// and (with __) in the environment.
		private static readonly Option<string>[] ConfigOptions =
		[
			FaolanCommandLine.ConfigOption("Database:Provider", "Sqlite or Postgres."),
			FaolanCommandLine.ConfigOption("Database:ConnectionString", "The database to serve from; built from faolan.sql on first run if it has no content."),
			FaolanCommandLine.ConfigOption("ServerIpAddress", "The address the login tier advertises to clients - must be reachable FROM the client."),
			FaolanCommandLine.ConfigOption("UniverseAgentPort", "Login-tier entry port (7010 is the client's fixed entry point)."),
			FaolanCommandLine.ConfigOption("PlayerAgentPort", "Login-tier player-agent port."),
			FaolanCommandLine.ConfigOption("WelcomeString", "Login MOTD."),
			FaolanCommandLine.ConfigOption("TerrainDir", "Baked terrain directory; without it ground-snap and line-of-sight are disabled."),
			FaolanCommandLine.ConfigOption("Mcp:Enabled", "Host the in-process MCP endpoint (default off when unset)."),
			FaolanCommandLine.ConfigOption("Mcp:Port", "MCP endpoint port."),
			FaolanCommandLine.ConfigOption("Mcp:Url", "MCP endpoint URL the chat bridge connects to."),
			FaolanCommandLine.ConfigOption("Log:File", "Log file path (default faolan.log in the CWD)."),
			FaolanCommandLine.ConfigOption("Log:PacketBytes", "Include the raw byte dump in packet log lines."),
			FaolanCommandLine.ConfigOption("Account:Premium", "Advertise every account as subscribed (default true)."),
			FaolanCommandLine.ConfigOption("Account:PremiumCharSlots", "Character slots advertised when premium."),
			FaolanCommandLine.ConfigOption("Account:FreeCharSlots", "Character slots advertised when not premium.")
		];

		// Returns null when the parser handled the invocation itself (--help, or it reported a parse error); the
		// caller should exit with `exitCode`.
		public static ServerOptions? Parse(string[] args, out int exitCode)
		{
			var command = new RootCommand("Project Faolan - Age of Conan server protocol emulator.") { Role };
			foreach (var option in ConfigOptions)
				command.Options.Add(option);

			var result = command.Parse(args);
			if (result.Errors.Count > 0 || args.Contains("--help") || args.Contains("-h") || args.Contains("-?"))
			{
				exitCode = result.Invoke();
				return null;
			}

			exitCode = 0;
			return new ServerOptions(result.GetValue(Role) ?? [], FaolanCommandLine.Overrides(result, ConfigOptions));
		}
	}

	public sealed record ServerOptions(string[] Roles, Dictionary<string, string?> ConfigOverrides);
}
