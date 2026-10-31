using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Extensions;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Tests
{
	// Shared test-DB context options, so the many tests that each spun up their own SQLite context stop duplicating
	// the `new DbContextOptionsBuilder<DatabaseContext>().UseSqlite(...)` boilerplate. Two shapes, matching how the
	// suite uses a DB: a writable throwaway DB the test seeds (Options), and the seeded game database read read-only
	// (ReadOnly - the real-data golden fixture: NPC spawns, quest journal, snapshots build from the shipped rows).
	//
	// The test DB is independent of the production provider. The DatabaseContext is code-first / provider-agnostic,
	// and the golden tests assert that our code builds the right bytes from the data - the storage engine underneath
	// is irrelevant to that. So even if production moves off SQLite (e.g. Postgres for the Docker multi-realm shared
	// DB), the plan for the tests is:
	//   1. (recommended) Keep an embedded SQLite fixture for the golden tests - `faolan.db` stays SQLite and these
	//      methods stay on SQLite, regardless of prod. Fast, no Docker in CI, no data export. The importer keeps a
	//      SQLite output path purely to regenerate the fixture (a `--provider sqlite` run) while emitting Postgres for
	//      prod. None of the ~10 real-data tests change.
	//   2. (only for provider fidelity) A small, separate Testcontainers-Postgres integration suite to catch
	//      provider-specific issues (ulong -> signed bigint, `INSERT OR IGNORE` -> `ON CONFLICT`, order by on ulong).
	//      Not the whole golden suite - that stays on SQLite per (1).
	// This class is the single seam: to change the strategy, edit only these two methods; the tests don't change.
	public static class TestDb
	{
		// An IDbContextFactory over a fixed options object, so a test can build a real DatabaseRepository against a
		// throwaway file DB. It existed as a byte-identical private copy in two test classes and was about to become a
		// third; the repository takes a factory, so every repository-level test needs one, and that makes it part of
		// this seam rather than a detail of whichever test needed it first.
		public sealed class ContextFactory(DbContextOptions<DatabaseContext> options) : IDbContextFactory<DatabaseContext>
		{
			public DatabaseContext CreateDbContext()
			{
				return new DatabaseContext(options);
			}

			public Task<DatabaseContext> CreateDbContextAsync(CancellationToken ct = default)
			{
				return Task.FromResult(new DatabaseContext(options));
			}
		}

		// The real-data fixture, built rather than checked in: migrations + faolan.sql, through the very same
		// DatabaseInitializer the server runs at startup. So the golden tests keep asserting against the shipped
		// game data without depending on a shipped binary - and if seeding ever breaks, these tests break with it.
		//
		// Cached by the seed's content hash under the temp dir: a repeat run reuses it (the build is ~150ms of seed
		// load plus a migrate, but free is better), and editing faolan.sql changes the hash so a stale fixture can
		// never be picked up. Built to a unique temp name and moved into place, so two concurrent test runs racing on
		// the same hash cannot observe a half-written database.
		//
		// NB it carries no character rows - the seed excludes them as per-developer play state. No test needs them
		// (verified: the whole suite passes against this fixture); one that does should create its own.
		public static string GameDatabase => Fixture.Value;

		private static readonly Lazy<string> Fixture = new Lazy<string>(BuildFixture, LazyThreadSafetyMode.ExecutionAndPublication);

		private static string BuildFixture()
		{
			var seed = TestPaths.Repo("faolan.sql");

			// Keyed on the seed and on the assembly that builds from it. Seed content alone is not enough: the fixture
			// is migrations + faolan.sql + DatabaseInitializer's non-seed rows (the default account and realm), and
			// those come from code. A change to the default account's password would leave every cached fixture stale
			// while a seed-only key stayed identical, and the suite would keep asserting against the stale row.
			var initializer = typeof(DatabaseInitializer).Assembly.Location;
			var key = SHA256.HashData([.. File.ReadAllBytes(seed), .. File.ReadAllBytes(initializer)]);
			var hash = Convert.ToHexStringLower(key)[..16];
			var directory = Path.Combine(Path.GetTempPath(), "faolan-tests");
			Directory.CreateDirectory(directory);

			var path = Path.Combine(directory, $"seed-{hash}.db");
			if (File.Exists(path))
			{
				// Mark it in use, so a concurrent run's prune (below) cannot take it out from under us.
				File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
				PruneStaleFixtures(directory, path);
				return path;
			}

			// DatabaseInitializer finds the seed through Global.SeedPath, which is CWD-relative - and the test host's
			// CWD is the test bin directory, not the repo root. Point it at the real one.
			Environment.SetEnvironmentVariable("FAOLAN_SEED", seed);

			var building = $"{path}.{Guid.NewGuid():N}.tmp";
			using (var db = new DatabaseContext(Options(building)))
			{
				DatabaseInitializer.InitializeAsync(db).GetAwaiter().GetResult();

				// Fold the write-ahead log back into the database file. Without this the move below takes the main
				// file and leaves its -wal behind - i.e. potentially an incomplete database - because the seed's
				// writes are still sitting in the log.
				db.Database.ExecuteSqlRaw("PRAGMA wal_checkpoint(TRUNCATE);");
			}

			// The context above is disposed by its `using`, and `Options` opens these databases with `Pooling=False`, so
			// that really closes the connection and the move below sees an unlocked, checkpointed file. Reaching into
			// the process-global pool with `SqliteConnection.ClearAllPools()` has the same effect - see Delete for why
			// that is the wrong tool in a parallel test run.
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

			PruneStaleFixtures(directory, path);
			return path;
		}

		// Evicts fixtures nobody is using. The cache above had none, and /tmp is a ramdisk on the dev machine: 625
		// fixtures at 17 MB accumulated to 6.6 GB, filled it, and the whole suite then failed with ENOSPC - 104 tests
		// at once, with nothing in the diff to explain it. Same failure the orphaned `-wal` files caused before
		// `Delete` was written, and a cache that never evicts is that bug wearing a useful face.
		//
		// It leaked per build, not per change. The key has to include the initializer assembly (see above), and this
		// build is not byte-deterministic - two consecutive `dotnet build`s with no source change produce
		// two different hashes - so every build mints a fixture. The window has to be short enough that a working day of
		// building cannot fill the disk. An hour keeps the cache useful for the case it actually serves (repeated test
		// runs against one build) while bounding the leak to one hour's worth.
		//
		// Never the fixture in hand, and never one touched inside the window: a run stamps the fixture it uses on a
		// cache hit, so a fresh timestamp means "in use", not merely "recent".
		private static void PruneStaleFixtures(string directory, string current)
		{
			var cutoff = DateTime.UtcNow - TimeSpan.FromHours(1);
			foreach (var file in Directory.EnumerateFiles(directory, "seed-*.db"))
			{
				if (file == current || File.GetLastWriteTimeUtc(file) > cutoff)
					continue;

				// The sidecars too - a WAL-mode database is three files, which is the leak `Delete` exists to stop.
				// Not routed through `Delete`: it clears the connection pool, and a fixture untouched for an hour is
				// in no pool of this process anyway.
				foreach (var part in new[] { file, $"{file}-wal", $"{file}-shm" })
				{
					try
					{
						if (File.Exists(part))
							File.Delete(part);
					}
					catch (IOException)
					{
						// a fixture another run still has open is not worth failing the suite over
					}
				}
			}
		}

		// A writable throwaway SQLite database at `path` (typically a temp file). Routed through the shared
		// UseFaolanDatabase (SQLite - the test fixture is always SQLite, see above) so it inherits the right migrations
		// assembly + settings. Opt into lazy-loading proxies for the few tests that need them.
		//
		// `Pooling=False`, and that is not a performance detail. Disposing a context otherwise returns its connection
		// to Microsoft.Data.Sqlite's pool rather than closing it, so the file stays open and `Delete` below cannot
		// remove it - which invites `SqliteConnection.ClearAllPools()` in Delete. That call is process-global, and
		// xUnit runs test classes in parallel: one class finishing its cleanup disposes the pooled connection another
		// class is mid-query on, and the suite fails intermittently with `ObjectDisposedException: SQLitePCL.sqlite3`
		// in whichever test happens to be running. Not pooling in the first place removes both the leak and the
		// cross-test reach.
		public static DbContextOptions<DatabaseContext> Options(string path, bool lazyLoadingProxies = false)
		{
			var builder = new DbContextOptionsBuilder<DatabaseContext>();
			builder.UseFaolanDatabase(FaolanDbProvider.Sqlite, $"Data Source={path};Pooling=False");
			if (lazyLoadingProxies)
				builder.UseLazyLoadingProxies();

			return builder.Options;
		}

		// Read-only options for an existing SQLite database at `path` - the seeded game-database golden fixture
		// (TestPaths.FaolanDb); read-only so a test can't mutate the shared data.
		public static DbContextOptions<DatabaseContext> ReadOnly(string path)
		{
			return DatabaseExtensions.BuildOptions(FaolanDbProvider.Sqlite, $"Data Source={path};Mode=ReadOnly");
		}

		// Deletes a throwaway test database and its sidecars.
		//
		// `File.Delete(path)` alone leaks. These databases run in WAL mode, so SQLite writes `<path>-wal` and
		// `<path>-shm` beside the main file, and deleting only the file leaves those behind - 3,856 orphaned `-wal`
		// files totalling 2.0 GB had accumulated in /tmp, which is a ramdisk here, and filled it until the whole
		// suite failed with ENOSPC. Deleting all three is what closes that.
		//
		// It does not clear the connection pool any more. `SqliteConnection.ClearAllPools()` is process-global and
		// xUnit runs test classes in parallel, so one class's cleanup disposed the pooled connection another class was
		// mid-query on - an intermittent `ObjectDisposedException: SQLitePCL.sqlite3` landing on whichever test was
		// unlucky. `Options` opens these databases with `Pooling=False` instead, so a disposed context really closes its
		// connection and there is no pool to reach into.
		public static void Delete(string path)
		{
			foreach (var file in new[] { path, $"{path}-wal", $"{path}-shm" })
			{
				try
				{
					if (File.Exists(file))
						File.Delete(file);
				}
				catch (IOException)
				{
					// a database another test still holds open is not worth failing a passing test over
				}
			}
		}

		// Seeds the world rows a Character depends on - Account, Realm, Map - and saves. Character has non-null FKs to
		// all three, plus not null columns on Realm, so nothing character-shaped inserts without them. Six tests had
		// each hand-rolled this same trio.
		//
		// The map defaults to TortageBeachId because that is what every one of them used; pass another where the map
		// itself is the subject.
		public static void SeedWorld(DatabaseContext ctx, uint accountId = 1, uint realmId = 1, uint mapId = Map.TortageBeachId)
		{
			ctx.Accounts.Add(new Account
			{
				Id = accountId,
				UserName = "t",
				Password = "t",
				LastIpAddress = ""
			});
			ctx.Realms.Add(new Realm
			{
				Id = realmId,
				Name = "Test",
				IpAddress = "127.0.0.1"
			});
			ctx.Maps.Add(new Map
			{
				Id = mapId,
				Name = "Test Map"
			});
			ctx.SaveChanges();
		}

		// SeedWorld plus a character on it, saved. For tests that need to reach a character-owned table
		// (CharacterFeat, CharacterSkill, CharacterQuest, ...) and do not care what the character is.
		//
		// Deliberately minimal - it exists to get a test to the table it actually cares about. A test that asserts on
		// the character itself should build its own, and one that needs several should call SeedWorld and add them.
		public static Character SeedCharacter(DatabaseContext ctx, uint characterId = 0x01000001, ConanClass characterClass = ConanClass.Barbarian, string name = "Testchar")
		{
			SeedWorld(ctx);

			var character = new Character
			{
				Id = characterId,
				AccountId = 1,
				RealmId = 1,
				MapId = Map.TortageBeachId,
				Name = name,
				Class = characterClass,
				Race = Race.Cimmerian,
				Sex = Sex.Male
			};
			ctx.Characters.Add(character);
			ctx.SaveChanges();
			return character;
		}

		// A writable throwaway database with SeedCharacter already applied - the common case, and the one that saves a
		// test from repeating the EnsureCreated + seed dance. The caller owns the file and should delete it.
		public static (DbContextOptions<DatabaseContext> Options, string Path) NewSeededDatabase(uint characterId = 0x01000001, ConanClass characterClass = ConanClass.Barbarian)
		{
			var path = Path.Combine(Path.GetTempPath(), $"faolan-test-{Path.GetRandomFileName()}.db");
			var options = Options(path);
			using (var ctx = new DatabaseContext(options))
			{
				ctx.Database.EnsureCreated();
				SeedCharacter(ctx, characterId, characterClass);
			}

			return (options, path);
		}
	}
}
