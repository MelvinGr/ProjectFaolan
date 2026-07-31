using System.IO;
using System.Runtime.CompilerServices;

namespace Faolan.Tests.Support
{
	// Repo-relative fixture paths, resolved from THIS file's compile-time location ([CallerFilePath]) - the same
	// trick Golden uses. Tests previously hardcoded an absolute checkout path, which pinned the whole suite to one
	// machine at one path: renaming the checkout (or cloning it anywhere else) silently broke 27 test files. Never
	// hardcode an absolute path in a test; go through here (or Golden).
	public static class TestPaths
	{
		// The real-data game database for tests. BUILT from the migrations + faolan.sql rather than read from the
		// committed faolan.db (#412) - see TestDb.GameDatabase, which is the single place that strategy lives.
		public static string FaolanDb => TestDb.GameDatabase;

		// The named capture blobs + render templates used as test INPUTS. They live in the fixture archive's `assets/`
		// keys (Faolan.Tests/Assets/fixtures.bin, the one file that holds every byte the suite reads) - `Golden` reads
		// the expected OUTPUTS out of the same archive.
		//
		// Asset() hands back a PATH because ~29 call sites pass one to something that opens it; the bytes are
		// materialized into the gitignored obj/ cache on first use. Prefer AssetBytes() in new code.
		public static string Asset(string fileName)
		{
			return FixtureArchive.NamedPath($"assets/{fileName}");
		}

		public static byte[] AssetBytes(string fileName)
		{
			return FixtureArchive.Named($"assets/{fileName}");
		}

		// Any repo-relative path, e.g. Repo("docs", "protocol", "gen", "opcodes.md").
		public static string Repo(params string[] parts)
		{
			var all = new string[parts.Length + 1];
			all[0] = RepoRoot();
			parts.CopyTo(all, 1);
			return Path.Combine(all);
		}

		private static string RepoRoot([CallerFilePath] string thisFile = "")
		{
			return Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(thisFile)!)!)!;
			// -> repo root
		}
	}
}
