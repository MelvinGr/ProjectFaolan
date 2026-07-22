using System;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Faolan.Core.Database.Extensions
{
	public static class DatabaseExtensions
	{
		// Options for a DatabaseContext on the given provider + connection, routed through UseFaolanDatabase - so no
		// site hand-rolls `new DbContextOptionsBuilder<DatabaseContext>().UseSqlite(...)` and drifts (e.g. a stale
		// MigrationsAssembly). Callers add extras (lazy-loading proxies) on the returned builder if needed.
		public static DbContextOptions<DatabaseContext> BuildOptions(FaolanDbProvider provider, string connection)
		{
			var builder = new DbContextOptionsBuilder<DatabaseContext>();
			builder.UseFaolanDatabase(provider, connection);
			return builder.Options;
		}

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

		// A ready DatabaseContext for the given provider + connection (the shared factory for one-off short-lived
		// contexts - reports, tools). Long-lived server/DI use goes through the pooled factory (ConfigureFaolanDatabase).
		public static DatabaseContext CreateContext(FaolanDbProvider provider, string connection)
		{
			return new DatabaseContext(BuildOptions(provider, connection));
		}

		// The one place the provider is chosen. Every context - server, importer, tests, design-time - routes its
		// DbContextOptions through here so a single switch covers them all. `connection` is a SQLite `Data Source=...`
		// string OR a full Npgsql connection string, per provider.
		public static DbContextOptionsBuilder UseFaolanDatabase(this DbContextOptionsBuilder builder, FaolanDbProvider provider, string connection)
		{
			// Each provider's migrations live in their own assembly (own model snapshot) - Migrate() then applies only
			// that provider's set. The apps reference both so EF can load the one named here at runtime.
			return provider switch
			{
				FaolanDbProvider.Postgres => builder.UseNpgsql(connection, x => x.MigrationsAssembly("Faolan.Core.Database.Migrations.Postgres").UseQuerySplittingBehavior(QuerySplittingBehavior.SingleQuery)),
				FaolanDbProvider.Sqlite => builder.UseSqlite(connection, x => x.MigrationsAssembly("Faolan.Core.Database.Migrations.Sqlite").UseQuerySplittingBehavior(QuerySplittingBehavior.SingleQuery)),
				_ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown database provider.")
			};
		}
	}
}
