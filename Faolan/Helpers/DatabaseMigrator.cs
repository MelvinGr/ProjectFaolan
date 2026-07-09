using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace Faolan.Helpers
{
	// Applies pending EF schema migrations on startup. The server does NOT seed data: the database is the single
	// source of truth (committed faolan.db). Game data is (re)built from the raw sources by the Faolan.Importer
	// tool, not embedded in the server. See docs/tooling/importer-plan.md.
	public class DatabaseMigrator : BackgroundService
	{
		private readonly IDbContextFactory<DatabaseContext> _contextFactory;

		public DatabaseMigrator(IDbContextFactory<DatabaseContext> contextFactory)
		{
			_contextFactory = contextFactory;
		}

		protected override async Task ExecuteAsync(CancellationToken cancellationToken)
		{
			await using var ctx = _contextFactory.CreateDbContext();
			await ctx.Database.MigrateAsync(cancellationToken);
		}
	}
}
