using System;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Extensions
{
	public static class FaolanDbProviderExtensions
	{
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
