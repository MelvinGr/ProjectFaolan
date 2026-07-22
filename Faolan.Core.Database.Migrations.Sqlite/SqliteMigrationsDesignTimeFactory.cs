using Faolan.Core;
using Faolan.Core.Database.Extensions;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Faolan.Core.Database.Migrations.Sqlite
{
	// Builds the DbContext for the EF tooling when scaffolding SQLite migrations into THIS project:
	//   dotnet ef migrations add <Name> --project Faolan.Core.Database.Migrations.Sqlite --startup-project Faolan.Core.Database.Migrations.Sqlite
	// The provider is fixed (this IS the SQLite migration project - it must scaffold SQLite regardless of what
	// faolan.json currently selects); the connection comes from the shared faolan.json (no hard-coded path).
	// `migrations add` never connects, so a Postgres-pointed config is fine here; `database update` takes --connection.
	public sealed class SqliteMigrationsDesignTimeFactory : IDesignTimeDbContextFactory<DatabaseContext>
	{
		public DatabaseContext CreateDbContext(string[] args)
		{
			var config = new ConfigurationBuilder().AddJsonFile(RepoPaths.Combine("faolan.json")).Build();
			return DatabaseServiceCollectionExtensions.CreateContext(FaolanDbProvider.Sqlite, config.DatabaseConnection);
		}
	}
}
