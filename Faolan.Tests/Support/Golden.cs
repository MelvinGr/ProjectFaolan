using System.IO;
using System.Runtime.CompilerServices;

namespace Faolan.Tests.Support
{
	// Loads golden fixtures (committed byte-exact reference captures) from Faolan.Tests/Golden. The Strie player
	// spawn template lives here too (SpawnStrieTemplate) - it is a TEST ORACLE only; the server generates player
	// spawns from the character's DB scaffold rows (PlayerSpawnScaffoldRows).
	public static class Golden
	{
		public static byte[] Bytes(string name)
		{
			return File.ReadAllBytes(Path.Combine(Dir(), name + ".bin"));
		}

		private static string Dir([CallerFilePath] string thisFile = "")
		{
			return Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(thisFile)!)!, "Golden");
		}
	}
}
