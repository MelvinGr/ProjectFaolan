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

		public static (FaolanDbProvider Provider, string Connection) ResolveDatabase(string[] args)
		{
			// The one command-line option this cares about, read DIRECTLY rather than by handing the whole argv to
			// AddCommandLine. That provider pairs SEQUENTIAL tokens, so any valueless flag swallows whatever follows
			// it - `--dry-run --connectionstring "X"` parsed as dry-run="--connectionstring" and the connection
			// string vanished, silently pointing the command at the CONFIGURED database instead of the named one.
			// Nothing here needs to understand the rest of the CLI's grammar, so it doesn't: no flag inventory to
			// keep in step, and no ordering rule for the caller to remember.
			var overrides = new Dictionary<string, string?>();
			var connection = Option(args, "--connectionstring");
			if (connection != null)
				overrides["Database:ConnectionString"] = connection;

			var config = new ConfigurationBuilder()
				.AddJsonFile(Global.ConfigPath, true)
				.AddInMemoryCollection(overrides)
				.AddEnvironmentVariables()
				.Build();

			return (config.DatabaseProvider, config.DatabaseConnection);
		}
	}
}
