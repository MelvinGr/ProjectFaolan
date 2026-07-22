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
			return provider switch
			{
				FaolanDbProvider.Postgres => builder.UseNpgsql(connection, x => x.MigrationsAssembly("Faolan").UseQuerySplittingBehavior(QuerySplittingBehavior.SingleQuery)),
				FaolanDbProvider.Sqlite => builder.UseSqlite(connection, x => x.MigrationsAssembly("Faolan").UseQuerySplittingBehavior(QuerySplittingBehavior.SingleQuery)),
				_ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown database provider.")
			};
		}
	}
}
