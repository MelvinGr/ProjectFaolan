using Faolan.Core.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Faolan.Core.Database.Migrations.Sqlite
{
	// Builds the DbContext for the EF tooling when scaffolding SQLite migrations into THIS project:
	//   dotnet ef migrations add <Name> --project Faolan.Core.Database.Migrations.Sqlite --startup-project Faolan.Core.Database.Migrations.Sqlite
	// Each provider's migrations live in their own project (own model snapshot), so incremental `migrations add`
	// works independently. The connection string is a placeholder - `migrations add` builds the model without
	// connecting; only `database update` needs a real one (pass --connection). MigrationsAssembly points at self.
	public sealed class SqliteMigrationsDesignTimeFactory : IDesignTimeDbContextFactory<DatabaseContext>
	{
		public DatabaseContext CreateDbContext(string[] args)
		{
			var options = new DbContextOptionsBuilder<DatabaseContext>()
				.UseSqlite("Data Source=faolan.db", x => x.MigrationsAssembly("Faolan.Core.Database.Migrations.Sqlite"))
				.Options;

			return new DatabaseContext(options);
		}
	}
}
