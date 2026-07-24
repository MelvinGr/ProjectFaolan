using System.Collections.Generic;
using Faolan.Core.Cli;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Microsoft.Extensions.Configuration;

namespace Faolan.Importer.Cli
{
	// Shared config for every importer entry point (import + reports), so a setting lives in ONE place and follows
	// the SAME precedence as the server / Faolan.Web: environment variables > command line > faolan.json (optional).
	// A Docker seed job can therefore target Postgres purely by env vars (Database__Provider /
	// Database__ConnectionString).
	public static class CliConfig
	{
		// faolan.json < command line < environment - the shared builder, so the importer resolves settings exactly
		// like the server does. The command-line half arrives ALREADY PARSED (ImporterCommand).
		public static IConfiguration Resolve(IDictionary<string, string?> commandLine)
		{
			return FaolanCommandLine.Build(commandLine);
		}

		public static (FaolanDbProvider Provider, string Connection) ResolveDatabase(IDictionary<string, string?> commandLine)
		{
			var config = Resolve(commandLine);
			return (config.DatabaseProvider, config.DatabaseConnection);
		}
	}
}
