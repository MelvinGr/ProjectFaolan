using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Faolan.Core.Database.Helpers
{
	// Applies pending EF schema migrations on startup. The server does NOT seed data: the database is the single
	// source of truth (committed faolan.db). Game data is (re)built from the raw sources by the Faolan.Importer
	// tool, not embedded in the server. See docs/tooling/importer.md.
	//
	// A plain IHostedService, deliberately NOT a BackgroundService: the host awaits each hosted service's
	// StartAsync in registration order, and this one is registered ahead of every listener - so migrating here
	// BLOCKS startup until the schema is current. A BackgroundService opts out of exactly that (its StartAsync
	// returns at the first await inside ExecuteAsync), which would let the listeners come up against a
	// half-migrated schema. Only holds if the host keeps sequential start (HostOptions.ServicesStartConcurrently
	// stays false, the default).
	public class DatabaseMigrator : IHostedService
	{
		private readonly IDbContextFactory<DatabaseContext> _contextFactory;
		private readonly ILogger<DatabaseMigrator> _logger;

		public DatabaseMigrator(IDbContextFactory<DatabaseContext> contextFactory, ILogger<DatabaseMigrator> logger)
		{
			_contextFactory = contextFactory;
			_logger = logger;
		}

		public async Task StartAsync(CancellationToken cancellationToken)
		{
			try
			{
				await using var ctx = await _contextFactory.CreateDbContextAsync(cancellationToken);

				// Migrate + seed the default realm/account - the shared sequence the importer runs too (DatabaseInitializer).
				var seeded = await DatabaseInitializer.InitializeAsync(ctx, cancellationToken);
				if (seeded.RealmCreated)
					_logger.LogInformation("No realm present - inserted the default realm.");
				if (seeded.AccountCreated)
					_logger.LogInformation("No account present - inserted the default account.");
			}
			catch (Exception ex)
			{
				// A failed migration leaves the schema half-applied; refuse to run on a broken DB rather than
				// limping on. Rethrowing from StartAsync aborts host startup outright - no listener ever binds, and
				// the exception surfaces from RunAsync (so no IHostApplicationLifetime.StopApplication needed).
				_logger.LogCritical(ex, "Database migration failed - refusing to start on a broken schema.");
				throw;
			}
		}

		public Task StopAsync(CancellationToken cancellationToken)
		{
			return Task.CompletedTask;
		}
	}
}
