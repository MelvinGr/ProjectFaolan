using System;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Extensions
{
	// The database backend Faolan runs on. SQLite is the default (the committed faolan.db seed, #379); Postgres is
	// the parallel option selected by "DatabaseProvider": "Postgres" in faolan.json. Everything above this line is
	// provider-agnostic - the model has no SQLite-isms and all access is EF - so the ONLY provider-specific code is
	// UseFaolanDatabase (the UseSqlite/UseNpgsql switch) and the uint/ulong conversions in DatabaseContext.
	public enum FaolanDbProvider
	{
		Sqlite,
		Postgres
	}

	public static class FaolanDbProviderExtensions
	{
		// The one place the provider is chosen. Every context - server, importer, tests, design-time - routes its
		// DbContextOptions through here so a single switch covers them all. `connection` is a SQLite `Data Source=...`
		// string OR a full Npgsql connection string, per provider.
		public static DbContextOptionsBuilder UseFaolanDatabase(this DbContextOptionsBuilder builder, FaolanDbProvider provider, string connection)
		{
			return provider switch
			{
				FaolanDbProvider.Postgres => builder.UseNpgsql(connection,
					x => x.MigrationsAssembly("Faolan").UseQuerySplittingBehavior(QuerySplittingBehavior.SingleQuery)),
				FaolanDbProvider.Sqlite => builder.UseSqlite(connection,
					x => x.MigrationsAssembly("Faolan").UseQuerySplittingBehavior(QuerySplittingBehavior.SingleQuery)),
				_ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown database provider.")
			};
		}

		// Parses the "DatabaseProvider" config value (case-insensitive; blank/unset -> Sqlite).
		public static FaolanDbProvider ParseProvider(string? value)
		{
			return string.IsNullOrWhiteSpace(value)
				? FaolanDbProvider.Sqlite
				: Enum.Parse<FaolanDbProvider>(value, ignoreCase: true);
		}
	}
}
