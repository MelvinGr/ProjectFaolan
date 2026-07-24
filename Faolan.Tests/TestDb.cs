using Faolan.Core.Database;
using Faolan.Core.Database.Extensions;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Tests
{
	// Shared test-DB context options, so the many tests that each spun up their own SQLite context stop duplicating
	// the `new DbContextOptionsBuilder<DatabaseContext>().UseSqlite(...)` boilerplate. Two shapes, matching how the
	// suite uses a DB: a writable throwaway DB the test seeds (Options), and the committed faolan.db read read-only
	// (ReadOnly - the real-data golden fixture: NPC spawns, quest journal, snapshots build from the committed rows).
	//
	// THE TEST DB IS INDEPENDENT OF THE PRODUCTION PROVIDER. The DatabaseContext is code-first / provider-agnostic,
	// and the golden tests assert that OUR CODE builds the right bytes from the DATA - the storage engine underneath
	// is irrelevant to that. So even if production moves off SQLite (e.g. Postgres for the Docker multi-realm shared
	// DB, #324), the plan for the tests is:
	//   1. (recommended) Keep an EMBEDDED SQLITE fixture for the golden tests - `faolan.db` stays SQLite and these
	//      methods stay on SQLite, regardless of prod. Fast, no Docker in CI, no data export. The importer keeps a
	//      SQLite output path purely to regenerate the fixture (a `--provider sqlite` run) while emitting Postgres for
	//      prod. None of the ~10 real-data tests change.
	//   2. (only for provider FIDELITY) A SMALL, separate Testcontainers-Postgres integration suite to catch
	//      provider-specific issues (ulong -> signed bigint, `INSERT OR IGNORE` -> `ON CONFLICT`, ORDER BY on ulong).
	//      NOT the whole golden suite - that stays on SQLite per (1).
	// THIS CLASS IS THE SINGLE SEAM: to change the strategy, edit only these two methods; the tests don't change.
	public static class TestDb
	{
		// A writable throwaway SQLite database at `path` (typically a temp file). Routed through the shared
		// UseFaolanDatabase (SQLite - the test fixture is always SQLite, see above) so it inherits the right migrations
		// assembly + settings. Opt into lazy-loading proxies for the few tests that need them.
		public static DbContextOptions<DatabaseContext> Options(string path, bool lazyLoadingProxies = false)
		{
			var builder = new DbContextOptionsBuilder<DatabaseContext>();
			builder.UseFaolanDatabase(FaolanDbProvider.Sqlite, $"Data Source={path}");
			if (lazyLoadingProxies)
				builder.UseLazyLoadingProxies();

			return builder.Options;
		}

		// Read-only options for an existing SQLite database at `path` - the committed faolan.db golden fixture
		// (TestPaths.FaolanDb); read-only so a test can't mutate the shared committed data.
		public static DbContextOptions<DatabaseContext> ReadOnly(string path)
		{
			return DatabaseExtensions.BuildOptions(FaolanDbProvider.Sqlite, $"Data Source={path};Mode=ReadOnly");
		}
	}
}
