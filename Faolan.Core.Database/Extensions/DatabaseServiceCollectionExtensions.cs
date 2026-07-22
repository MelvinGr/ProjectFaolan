using Faolan.Core.Database.Repositories;
using Faolan.Core.Extensions;
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
			// SingleQuery splitting (explicit): our multi-collection Includes (GetQuestDefinition = Goals + TextRefs, the
			// NpcTemplate load = Spells + Equipment + Morphs) all have SMALL collections and run per-quest-check /
			// per-NPC-at-zone-load, so one query (a tiny cartesian, ONE round-trip) beats split (a round-trip per
			// collection). Stating it also silences EF's QuerySplittingBehavior warning (which fires when it's unset).
			services.AddPooledDbContextFactory<DatabaseContext>(options =>
			{
				options.UseFaolanDatabase(configuration.DatabaseProvider, configuration.DatabaseConnection);
			});

			services.AddSingleton<IDatabaseRepository, DatabaseRepository>();

			return services;
		}
	}
}
