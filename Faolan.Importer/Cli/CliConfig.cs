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
		// The command-line config provider pairs SEQUENTIAL tokens, so a valueless flag swallows whatever follows it:
		// `--dry-run --connectionstring "X"` parses as dry-run="--connectionstring" and the connection string is
		// silently ignored, leaving the command pointed at the configured database instead of the one you named.
		// The importer's flags are all valueless, so drop any token that is immediately followed by another `--`
		// token (or ends the list) before the provider sees them.
		private static string[] WithoutValuelessFlags(string[] args)
		{
			return args
				.Where((a, i) => !(a.StartsWith("--", StringComparison.Ordinal) && !a.Contains('=') && (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))))
				.ToArray();
		}

		public static (FaolanDbProvider Provider, string Connection) ResolveDatabase(string[] args)
		{
			var config = new ConfigurationBuilder()
				.AddJsonFile(Global.ConfigPath, true)
				.AddCommandLine(WithoutValuelessFlags(args), new Dictionary<string, string>
				{
					["--connectionstring"] = "Database:ConnectionString"
				})
				.AddEnvironmentVariables()
				.Build();

			return (config.DatabaseProvider, config.DatabaseConnection);
		}
	}
}
