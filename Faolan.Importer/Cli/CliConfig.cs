using System.Collections.Generic;
using Faolan.Core;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Microsoft.Extensions.Configuration;

namespace Faolan.Importer.Cli
{
	// Shared DB config for every importer entry point (import + reports), so the connection lives in ONE place and
	// follows the SAME precedence as the server / Faolan.Web: environment variables > command line > faolan.json
	// (optional). The provider always comes from the config; `--db` is a DatabaseConnectionString override, mapped
	// onto that key so it slots into the command-line layer (over json, under env) instead of being a bespoke flag.
	// A Docker seed job can therefore target Postgres purely by env vars (DatabaseProvider / DatabaseConnectionString).
	public static class CliConfig
	{
		public static (FaolanDbProvider Provider, string Connection) ResolveDatabase(string[] args)
		{
			var config = new ConfigurationBuilder()
				.AddJsonFile(RepoPaths.Combine("faolan.json"), optional: true)
				.AddCommandLine(args, new Dictionary<string, string> { ["--db"] = "DatabaseConnectionString" })
				.AddEnvironmentVariables()
				.Build();

			return (config.DatabaseProvider, config.DatabaseConnection);
		}
	}
}
