using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Microsoft.Extensions.Configuration;

namespace Faolan.Importer.Cli
{
	// Shared DB config for every importer entry point (import + reports), so the connection lives in ONE place and
	// follows the SAME precedence as the server / Faolan.Web: environment variables > command line > faolan.json
	// (optional). The provider always comes from the config; `--connectionstring` is a Database:ConnectionString
	// override, mapped onto that key so it slots into the command-line layer (over json, under env).
	// A Docker seed job can therefore target Postgres purely by env vars (Database__Provider / Database__ConnectionString).
	public static class CliConfig
	{
		// `--name value`, or null when absent. A trailing `--name` with nothing after it counts as absent.
		private static string? Option(string[] args, string name)
		{
			var index = Array.IndexOf(args, name);
			return index >= 0 && index + 1 < args.Length
				? args[index + 1]
				: null;
		}

		// The importer's configuration: faolan.json < command line < environment, with the command-line key being the
		// config path itself. Callers that want the DB pair use ResolveDatabase.
		public static IConfiguration Resolve(string[] args)
		{
			// The one command-line option this cares about, read DIRECTLY rather than by handing the whole argv to
			// AddCommandLine. That provider pairs SEQUENTIAL tokens, so any valueless flag swallows whatever follows
			// it - `--dry-run --connectionstring "X"` parsed as dry-run="--connectionstring" and the connection
			// string vanished, silently pointing the command at the CONFIGURED database instead of the named one.
			// Nothing here needs to understand the rest of the CLI's grammar, so it doesn't: no flag inventory to
			// keep in step, and no ordering rule for the caller to remember.
			// One NAME per setting, whatever the input method: the command-line key IS the config path, so
			// `--Database:ConnectionString x` on the command line, `Database__ConnectionString` in the environment
			// and `Database: { ConnectionString }` in faolan.json all name the same thing. No short aliases - they
			// were a third spelling to keep in step (#412).
			var overrides = new Dictionary<string, string?>();
			foreach (var key in new[] { "Database:ConnectionString", "Database:Provider", "RdbDir" })
			{
				if (Option(args, $"--{key}") is { } value)
					overrides[key] = value;
			}

			var config = new ConfigurationBuilder()
				.AddJsonFile(Global.ConfigPath, true)
				.AddInMemoryCollection(overrides)
				.AddEnvironmentVariables()
				.Build();

			return config;
		}

		public static (FaolanDbProvider Provider, string Connection) ResolveDatabase(string[] args)
		{
			var config = Resolve(args);
			return (config.DatabaseProvider, config.DatabaseConnection);
		}
	}
}
