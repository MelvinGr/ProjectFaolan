using System;
using System.IO;
using Faolan.Core.Cli;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Enums;
using Microsoft.Data.Sqlite;
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
			// boundary touched only at save/load points. So contexts are short-lived, one per unit of work, created
			// from a pooled factory - never held open across a connection or game tick (which would leak/stale and
			// is not thread-safe). The repository owns all context lifetime via this factory.
			// SingleQuery splitting (explicit): our multi-collection Includes (GetQuestDefinition = Goals + TextRefs, the
			// NpcTemplate load = Spells + Equipment + Morphs) all have small collections and run per-quest-check /
			// per-NPC-at-zone-load, so one query (a tiny cartesian, one round-trip) beats split (a round-trip per
			// collection). Stating it also silences EF's QuerySplittingBehavior warning (which fires when it's unset).
			services.AddPooledDbContextFactory<DatabaseContext>(options => { options.UseFaolanDatabase(configuration.DatabaseProvider, configuration.DatabaseConnectionString); });

			services.AddSingleton<IDatabaseRepository, DatabaseRepository>();

			return services.AddRepositorySlices();
		}

		/// <summary>
		///     Forwards every per-domain slice of <see cref="IDatabaseRepository" /> (<see cref="IGuildRepository" />,
		///     <see cref="ICatalogRepository" />, ...) to the one registered repository, so a collaborator can take the
		///     narrow interface it calls and still get the shared singleton.
		/// </summary>
		/// <remarks>
		///     MS.DI does not infer that one registration satisfies a base interface. The slices are read off the
		///     composite's own interface list, so a new slice is forwarded without anyone having to remember to add it.
		///     The composition tests call this after stubbing <see cref="IDatabaseRepository" />.
		/// </remarks>
		public static IServiceCollection AddRepositorySlices(this IServiceCollection services)
		{
			foreach (var slice in typeof(IDatabaseRepository).GetInterfaces())
				services.AddSingleton(slice, sp => sp.GetRequiredService<IDatabaseRepository>());

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
		// string or a full Npgsql connection string, per provider.
		public static DbContextOptionsBuilder UseFaolanDatabase(this DbContextOptionsBuilder builder, FaolanDbProvider provider, string connection)
		{
			// Each provider's migrations live in their own assembly (own model snapshot) - Migrate() then applies only
			// that provider's set. The apps reference both so EF can load the one named here at runtime.
			return provider switch
			{
				FaolanDbProvider.Postgres => builder.UseNpgsql(connection,
					x => x.MigrationsAssembly("Faolan.Core.Database.Migrations.Postgres").UseQuerySplittingBehavior(QuerySplittingBehavior.SingleQuery)),
				FaolanDbProvider.Sqlite => builder.UseSqlite(ResolveSqliteDataSource(connection),
					x => x.MigrationsAssembly("Faolan.Core.Database.Migrations.Sqlite").UseQuerySplittingBehavior(QuerySplittingBehavior.SingleQuery)),
				_ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown database provider.")
			};
		}

		// A relative SQLite `Data Source` means "beside faolan.json", not "in the process CWD". Resolved here
		// because this is already the one place the provider is chosen, so every entry point - server, importer,
		// Faolan.Web, the EF design-time factories - gets it from one edit.
		//
		// Left alone: an absolute path, `:memory:`, a `file:` uri, and an empty source. Those either mean something
		// other than a file beside the config or are already unambiguous, and rewriting them would be the kind of
		// helpfulness that breaks a shared temp-file fixture.
		internal static string ResolveSqliteDataSource(string connection)
		{
			var builder = new SqliteConnectionStringBuilder(connection);
			var source = builder.DataSource;
			if (string.IsNullOrEmpty(source) || source == ":memory:" || source.StartsWith("file:", StringComparison.OrdinalIgnoreCase) || Path.IsPathRooted(source))
				return connection;

			builder.DataSource = Path.GetFullPath(Path.Combine(Global.ConfigDirectory, source));
			return builder.ConnectionString;
		}
	}
}
