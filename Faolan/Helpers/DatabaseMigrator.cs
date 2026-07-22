using System;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Faolan.Helpers
{
	// Applies pending EF schema migrations on startup. The server does NOT seed data: the database is the single
	// source of truth (committed faolan.db). Game data is (re)built from the raw sources by the Faolan.Importer
	// tool, not embedded in the server. See docs/tooling/importer.md.
	public class DatabaseMigrator : BackgroundService
	{
		private readonly IDbContextFactory<DatabaseContext> _contextFactory;
		private readonly IHostApplicationLifetime _lifetime;
		private readonly ILogger<DatabaseMigrator> _logger;

		public DatabaseMigrator(IDbContextFactory<DatabaseContext> contextFactory, IHostApplicationLifetime lifetime, ILogger<DatabaseMigrator> logger)
		{
			_contextFactory = contextFactory;
			_lifetime = lifetime;
			_logger = logger;
		}

		protected override async Task ExecuteAsync(CancellationToken cancellationToken)
		{
			try
			{
				await using var ctx = _contextFactory.CreateDbContext();

				// SQLite is the migration-tracked seed store (the committed faolan.db). The Postgres prototype has no
				// migration set yet, so it materialises the schema from the model with EnsureCreated (seed data comes
				// from re-running Faolan.Importer against it) - a parallel Postgres migrations assembly is the follow-up.
				if (ctx.Database.IsNpgsql())
					await ctx.Database.EnsureCreatedAsync(cancellationToken);
				else
					await ctx.Database.MigrateAsync(cancellationToken);

				// A world can't serve without a realm (guild/AH/mail/character FK it, #324) or an account to log into.
				// Both are config state, not schema, so ensure the defaults here rather than shipping them in the
				// migration - the same seed the importer applies to a fresh DB (DefaultSeeder). Idempotent.
				if (await DefaultSeeder.EnsureDefaultRealmAsync(ctx, cancellationToken))
					_logger.LogInformation("No realm present - inserted the default realm.");
				if (await DefaultSeeder.EnsureDefaultAccountAsync(ctx, cancellationToken))
					_logger.LogInformation("No account present - inserted the default account.");
			}
			catch (Exception ex)
			{
				// A failed migration leaves the schema half-applied; refuse to run on a broken DB rather than
				// limping on (a BackgroundService exception is otherwise swallowed and the server keeps going).
				_logger.LogCritical(ex, "Database migration failed - stopping the server to avoid running on a broken schema.");
				_lifetime.StopApplication();
				throw;
			}
		}
	}
}
