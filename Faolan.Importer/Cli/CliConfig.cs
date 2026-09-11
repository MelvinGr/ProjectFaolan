using Faolan.Core.Cli;
using Faolan.Core.Enums;
using Microsoft.Extensions.Configuration;

namespace Faolan.Importer.Cli
{
	/// <summary>
	///     Names the provider and connection string the importer's database access needs.
	/// </summary>
	/// <remarks>
	///     The config comes from the same shared stack as the server and Faolan.Web: defaults, faolan.json,
	///     faolan.user.json, command line, environment, in that order of precedence. A Docker seed job can therefore
	///     target Postgres purely through environment variables (Database__Provider, Database__ConnectionString).
	///     The stack is built once, by the parse (ImporterCommand into FaolanCommandLine.Build), and travels as
	///     ImporterOptions.Configuration, so nothing here re-reads the json files or the environment.
	/// </remarks>
	public static class CliConfig
	{
		public static (FaolanDbProvider Provider, string Connection) ResolveDatabase(IConfiguration config)
		{
			return (config.DatabaseProvider, config.DatabaseConnectionString);
		}
	}
}
