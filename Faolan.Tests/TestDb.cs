using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using Faolan.Core.Database;
using Faolan.Core.Database.Extensions;
using Faolan.Core.Enums;
using Faolan.Tests.Support;
using Microsoft.Data.Sqlite;
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
		// The real-data fixture, BUILT rather than checked in (#412): migrations + faolan.sql, through the very same
		// DatabaseInitializer the server runs at startup. So the golden tests keep asserting against the shipped
		// game DATA without depending on a shipped BINARY - and if seeding ever breaks, these tests break with it.
		//
		// Cached by the seed's content hash under the temp dir: a repeat run reuses it (the build is ~150ms of seed
		// load plus a migrate, but free is better), and editing faolan.sql changes the hash so a stale fixture can
		// never be picked up. Built to a unique temp name and MOVED into place, so two concurrent test runs racing on
		// the same hash cannot observe a half-written database.
		//
		// NB it carries no CHARACTER rows - the seed excludes them as per-developer play state. No test needs them
		// (verified: the whole suite passes against this fixture); one that does should create its own.
		public static string GameDatabase => Fixture.Value;

		private static readonly Lazy<string> Fixture = new(BuildFixture, LazyThreadSafetyMode.ExecutionAndPublication);

		private static string BuildFixture()
		{
			var seed = TestPaths.Repo("faolan.sql");

			// Keyed on the seed AND on the assembly that builds from it. Seed content alone is not enough: the fixture
			// is migrations + faolan.sql + DatabaseInitializer's non-seed rows (the default account and realm), and
			// those come from CODE. Changing the default account's password left every cached fixture stale while the
			// key stayed identical, so the suite kept asserting against the old row and passed.
			var initializer = typeof(DatabaseInitializer).Assembly.Location;
			var key = SHA256.HashData([.. File.ReadAllBytes(seed), .. File.ReadAllBytes(initializer)]);
			var hash = Convert.ToHexStringLower(key)[..16];
			var directory = Path.Combine(Path.GetTempPath(), "faolan-tests");
			Directory.CreateDirectory(directory);

			var path = Path.Combine(directory, $"seed-{hash}.db");
			if (File.Exists(path))
				return path;

			// DatabaseInitializer finds the seed through Global.SeedPath, which is CWD-relative - and the test host's
			// CWD is the test bin directory, not the repo root. Point it at the real one.
			Environment.SetEnvironmentVariable("FAOLAN_SEED", seed);

			var building = $"{path}.{Guid.NewGuid():N}.tmp";
			using (var db = new DatabaseContext(Options(building)))
			{
				DatabaseInitializer.InitializeAsync(db).GetAwaiter().GetResult();

				// Fold the write-ahead log back into the database file. Without this the move below takes the main
				// file and LEAVES ITS -wal BEHIND - i.e. potentially an incomplete database - because the seed's
				// writes are still sitting in the log.
				db.Database.ExecuteSqlRaw("PRAGMA wal_checkpoint(TRUNCATE);");
			}

			// Disposing the context returns its connection to Microsoft.Data.Sqlite's POOL rather than closing it, so
			// the file (and any -wal/-shm) stays open and the move can fail or copy a locked, mid-checkpoint file.
			SqliteConnection.ClearAllPools();

			try
			{
				File.Move(building, path);
			}
			catch (IOException) when (File.Exists(path))
			{
				File.Delete(building); // another run won the race - its database is just as good
			}
			finally
			{
				foreach (var sidecar in new[] { $"{building}-wal", $"{building}-shm" })
				{
					if (File.Exists(sidecar))
						File.Delete(sidecar);
				}
			}

			return path;
		}

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
