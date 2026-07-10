using Faolan.Core.Database;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Tests
{
	// Shared test-DB context options, so the many tests that each spun up their own SQLite context stop duplicating
	// the `new DbContextOptionsBuilder<DatabaseContext>().UseSqlite(...)` boilerplate. Two shapes, matching how the
	// suite uses a DB (see docs discussion): a writable throwaway DB the test seeds, and the committed faolan.db read
	// read-only (the real-data golden fixture). Kept on SQLite exactly as before - this is a de-dup, not a behaviour
	// change; the test DB is independent of whatever provider production runs.
	public static class TestDb
	{
		// A writable throwaway SQLite database at `path` (typically a temp file). The Faolan migrations assembly is
		// wired so EnsureCreated/Migrate-based tests work. Opt into lazy-loading proxies for the few that need them.
		public static DbContextOptions<DatabaseContext> Options(string path, bool lazyLoadingProxies = false)
		{
			var builder = new DbContextOptionsBuilder<DatabaseContext>().UseSqlite($"Data Source={path}", x => x.MigrationsAssembly("Faolan"));
			if (lazyLoadingProxies)
				builder.UseLazyLoadingProxies();

			return builder.Options;
		}

		// Read-only options for an existing SQLite database at `path` - the committed faolan.db golden fixture
		// (TestPaths.FaolanDb); read-only so a test can't mutate the shared committed data.
		public static DbContextOptions<DatabaseContext> ReadOnly(string path)
		{
			return new DbContextOptionsBuilder<DatabaseContext>().UseSqlite($"Data Source={path};Mode=ReadOnly").Options;
		}
	}
}
