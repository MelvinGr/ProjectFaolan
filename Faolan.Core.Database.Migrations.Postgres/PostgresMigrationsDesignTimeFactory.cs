using Faolan.Core.Database.Extensions;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Faolan.Core.Database.Migrations.Postgres
{
	// Builds the DbContext for the EF tooling when scaffolding Postgres migrations into THIS project:
	//   dotnet ef migrations add <Name> --project Faolan.Core.Database.Migrations.Postgres --startup-project Faolan.Core.Database.Migrations.Postgres
	// The provider is fixed (this IS the Postgres migration project - it must scaffold Postgres regardless of what
	// faolan.json currently selects); the connection comes from the shared faolan.json (no hard-coded host). `migrations
	// add` never connects, so a SQLite-pointed config is fine here; `database update` takes --connection.
	public sealed class PostgresMigrationsDesignTimeFactory : IDesignTimeDbContextFactory<DatabaseContext>
	{
		public DatabaseContext CreateDbContext(string[] args)
		{
			var config = new ConfigurationBuilder().AddJsonFile(RepoPaths.Combine("faolan.json")).Build();
			return DatabaseExtensions.CreateContext(FaolanDbProvider.Postgres, config.DatabaseConnection);
		}
	}
}
