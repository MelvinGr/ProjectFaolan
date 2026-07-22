using System.Collections.Generic;
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
		public static (FaolanDbProvider Provider, string Connection) ResolveDatabase(string[] args)
		{
			var config = new ConfigurationBuilder()
				.AddJsonFile(RepoPaths.Combine("faolan.json"), optional: true)
				.AddCommandLine(args, new Dictionary<string, string> { ["--connectionstring"] = "Database:ConnectionString" })
				.AddEnvironmentVariables()
				.Build();

			return (config.DatabaseProvider, config.DatabaseConnection);
		}
	}
}
