using Faolan.Core.Cli;
using Faolan.Core.Database.Extensions;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Faolan.Core.Database.Migrations.Sqlite
{
	// Builds the DbContext for the EF tooling when scaffolding SQLite migrations into this project:
	//   dotnet ef migrations add <Name> --project Faolan.Core.Database.Migrations.Sqlite --startup-project Faolan.Core.Database.Migrations.Sqlite
	// The provider is fixed (this is the SQLite migration project - it must scaffold SQLite regardless of what
	// faolan.json currently selects); the connection comes from the shared faolan.json (no hard-coded path).
	// The EF tools run design time with the CWD set to the project directory, not the repo root. Global.ConfigPath
	// searches upwards for faolan.json and a relative SQLite `Data Source` resolves against the directory it was found
	// in, so the commands work from anywhere in the tree; FAOLAN_CONFIG (or Database__ConnectionString) still overrides.
	// `migrations add` never connects, so a Postgres-pointed config is fine here.
	public sealed class SqliteMigrationsDesignTimeFactory : IDesignTimeDbContextFactory<DatabaseContext>
	{
		public DatabaseContext CreateDbContext(string[] args)
		{
			var config = new ConfigurationBuilder().AddJsonFile(Global.ConfigPath, true).AddEnvironmentVariables().Build();
			return DatabaseExtensions.CreateContext(FaolanDbProvider.Sqlite, config.DatabaseConnectionString);
		}
	}
}
