using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Faolan.Tests.Support
{
	// Repo-relative fixture paths, resolved from THIS file's compile-time location ([CallerFilePath]) - the same
	// trick Golden uses. Tests previously hardcoded "/home/melvin/Development/C#/Project Faolan/...", which pinned
	// the whole suite to one machine at one path: renaming the checkout (or cloning it anywhere else) silently
	// broke 27 test files. Never hardcode an absolute path in a test; go through here (or Golden).
	public static class TestPaths
	{
		// Faolan.Tests/Assets - capture blobs + render templates used as test INPUTS (Golden/ holds the expected
		// OUTPUTS). The four strie-login-*/bigboy captures live here because only the test suite reads them.
		public static string Asset(string fileName) => Path.Combine(TestProjectDir(), "Assets", fileName);

		public static byte[] AssetBytes(string fileName) => File.ReadAllBytes(Asset(fileName));

		// Captures/ - the packet-trace corpus. The *.tshark.json files are read at TEST time (the whole-corpus
		// round-trip gates), so they are fixtures, not regenerable build artifacts.
		public static string CapturesDir => Path.Combine(RepoRoot(), "Captures");

		public static string Capture(string fileName) => Path.Combine(CapturesDir, fileName);

		// The committed game database at the repo root.
		public static string FaolanDb => Path.Combine(RepoRoot(), "faolan.db");

		// Any repo-relative path, e.g. Repo("docs", "protocol", "gen", "opcodes.md").
		public static string Repo(params string[] parts)
		{
			var all = new string[parts.Length + 1];
			all[0] = RepoRoot();
			parts.CopyTo(all, 1);
			return Path.Combine(all);
		}

		private static string TestProjectDir([CallerFilePath] string thisFile = "")
			=> Path.GetDirectoryName(Path.GetDirectoryName(thisFile)!)!; // Support/ -> Faolan.Tests/

		private static string RepoRoot([CallerFilePath] string thisFile = "")
			=> Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(thisFile)!)!)!; // -> repo root
	}
}
