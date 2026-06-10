using Faolan.Core.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Faolan.Helpers
{
	// Required by the EF tooling (`dotnet ef migrations ...`) to build the DbContext at design time. The
	// runtime registration in ConfigureFaolanDatabase is a normal AddDbContext - EF tooling can't use it,
	// and building the host at design time throws (Statics reads build-time assembly metadata that isn't
	// present under the tooling). This factory is only used by the tooling; the app builds its context
	// through the host as usual.
	public sealed class DatabaseContextDesignTimeFactory : IDesignTimeDbContextFactory<DatabaseContext>
	{
		public DatabaseContext CreateDbContext(string[] args)
		{
			var options = new DbContextOptionsBuilder<DatabaseContext>().UseSqlite("Data Source=faolan.db", sqlite => sqlite.MigrationsAssembly("Faolan"))
				.Options;

			return new DatabaseContext(options);
		}
	}
}
