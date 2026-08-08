using Faolan.Core.Cli;
using Faolan.Core.Enums;
using Microsoft.Extensions.Configuration;

namespace Faolan.Importer.Cli
{
	// The importer's DB config comes from the same shared stack as the server / Faolan.Web:
	// defaults < faolan.json < faolan.user.json < command line < environment. A Docker seed job can therefore target
	// Postgres purely by env vars (Database__Provider / Database__ConnectionString).
	//
	// The stack itself is built ONCE, by the parse (ImporterCommand -> FaolanCommandLine.Build), and travels as
	// ImporterOptions.Configuration. This class used to take the override LAYER and re-resolve it - which meant eight
	// call sites each re-read both json files and the environment to look up one key - so all that is left is naming
	// the pair the DB needs.
	public static class CliConfig
	{
		public static (FaolanDbProvider Provider, string Connection) ResolveDatabase(IConfiguration config)
		{
			return (config.DatabaseProvider, config.DatabaseConnectionString);
		}
	}
}
