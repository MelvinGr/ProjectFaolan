using Faolan.Core.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Faolan.Core.Database.Migrations.Postgres
{
	// Builds the DbContext for the EF tooling when scaffolding Postgres migrations into THIS project:
	//   dotnet ef migrations add <Name> --project Faolan.Core.Database.Migrations.Postgres --startup-project Faolan.Core.Database.Migrations.Postgres
	// Own project = own model snapshot, so incremental `migrations add` works independently of the SQLite set. The
	// connection string is a placeholder - `migrations add` builds the model without connecting; only `database
	// update` needs a reachable server (pass --connection). MigrationsAssembly points at self.
	public sealed class PostgresMigrationsDesignTimeFactory : IDesignTimeDbContextFactory<DatabaseContext>
	{
		public DatabaseContext CreateDbContext(string[] args)
		{
			var options = new DbContextOptionsBuilder<DatabaseContext>()
				.UseNpgsql("Host=localhost;Database=faolan", x => x.MigrationsAssembly("Faolan.Core.Database.Migrations.Postgres"))
				.Options;

			return new DatabaseContext(options);
		}
	}
}
