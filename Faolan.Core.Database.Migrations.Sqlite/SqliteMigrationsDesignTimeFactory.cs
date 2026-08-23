using Faolan.Core.Cli;
using Faolan.Core.Database.Extensions;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Faolan.Core.Database.Migrations.Sqlite
{
	// Builds the DbContext for the EF tooling when scaffolding SQLite migrations into THIS project:
	//   dotnet ef migrations add <Name> --project Faolan.Core.Database.Migrations.Sqlite --startup-project Faolan.Core.Database.Migrations.Sqlite
	// The provider is fixed (this IS the SQLite migration project - it must scaffold SQLite regardless of what
	// faolan.json currently selects); the connection comes from the shared faolan.json (no hard-coded path).
	// GOTCHA: the EF tools run design time with the CWD set to the PROJECT directory, not the repo root, so the plain
	// relative config path misses - point FAOLAN_CONFIG at it (or set Database__ConnectionString in the environment):
	//   FAOLAN_CONFIG=$PWD/faolan.json dotnet ef migrations add <Name> --project ... --startup-project ...
	// `migrations add` never connects, so a Postgres-pointed config is fine here; `database update` takes --connection.
	public sealed class SqliteMigrationsDesignTimeFactory : IDesignTimeDbContextFactory<DatabaseContext>
	{
		public DatabaseContext CreateDbContext(string[] args)
		{
			var config = new ConfigurationBuilder().AddJsonFile(Global.ConfigPath, true).AddEnvironmentVariables().Build();
			return DatabaseExtensions.CreateContext(FaolanDbProvider.Sqlite, config.DatabaseConnectionString);
		}
	}
}
