namespace Faolan.Tests.Support
{
	// Loads golden fixtures (committed byte-exact reference captures) out of the fixture archive's `golden/` keys.
	// They were loose files under Faolan.Tests/Golden until they moved into Faolan.Tests/Assets/fixtures.bin, so that
	// every byte the suite reads lives in one committed file. The Strie player spawn template is here too
	// (SpawnAssassinTemplate) - it is a test oracle only; the server generates player spawns from the character's DB
	// scaffold rows (PlayerSpawnScaffoldRows).
	public static class Golden
	{
		public static byte[] Bytes(string name)
		{
			return FixtureArchive.Named($"golden/{name}.bin");
		}
	}
}
