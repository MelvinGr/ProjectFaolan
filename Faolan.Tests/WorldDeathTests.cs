using System;
using Faolan.GameServer.World;
using Xunit;

namespace Faolan.Tests
{
	// Byte-exact golden for the SimpleCharKilledByUpdateIIR (0xB066AFC2) kill record decoded in #214/#204 - the
	// explicit "this entity died" trigger the client needs to play the death anim / show the release screen.
	// Reproduces the record from AssassinL23_..._resurect (the player-death sequence before the 0x2024 release screen).
	public class WorldDeathTests
	{
		[Fact]
		public void BuildKilledBy_ReproducesCapturedRecord_ByteExact()
		{
			// The IIR record (the carrier prepends the u32 length): dying handle 0x08006F31, killer endpoint 0x000406F9.
			var body = WorldDeath.BuildKilledBy(0x08006F31, 0x000406F9);
			Assert.Equal("b066afc20000c35008006f3100010000c350000406f9", Convert.ToHexString(body).ToLowerInvariant());
		}
	}
}
