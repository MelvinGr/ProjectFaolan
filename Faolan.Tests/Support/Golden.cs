namespace Faolan.Tests.Support
{
	// Loads golden fixtures (byte-exact reference captures) out of the fixture archive's `golden/` keys, so that every
	// byte the suite reads lives in one file (FixtureArchive). The Strie player spawn template is here too
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
