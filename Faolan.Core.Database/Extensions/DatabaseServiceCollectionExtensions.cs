using Faolan.Core.Database.Repositories;
using Faolan.Core.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Faolan.Core.Database.Extensions
{
	public static class DatabaseServiceCollectionExtensions
	{
		public static IServiceCollection ConfigureFaolanDatabase(this IServiceCollection services, IConfiguration configuration)
		{
			// Game-server persistence model: live state lives in memory (the session); the DB is a persistence
			// boundary touched only at save/load points. So contexts are SHORT-LIVED, one per unit of work, created
			// from a pooled factory - never held open across a connection or game tick (which would leak/stale and
			// is not thread-safe). The repository owns all context lifetime via this factory.
			services.AddPooledDbContextFactory<DatabaseContext>(options => { options.UseSqlite($"Data Source={configuration.DatabasePath}", x => x.MigrationsAssembly("Faolan")); });

			services.AddSingleton<IDatabaseRepository, DatabaseRepository>();

			return services;
		}
	}
}
