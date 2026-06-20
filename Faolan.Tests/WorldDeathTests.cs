using System;
using System.Numerics;
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

		[Fact]
		public void BuildDeathEffect_ReproducesCapturedRecord_ByteExact()
		{
			// The self-applied death-state ApplySpell (0x6B87E316, spell 0x0009F000) from AssassinL23_..._resurect pkt#4051,
			// applyTo == applyFrom == the dying player 0x08006F31 (the carrier prepends the u32 length 0x00000050).
			var body = WorldDeath.BuildDeathEffect(0x08006F31);
			Assert.Equal(
				"6b87e3160000c35008006f3100000009f0000000c35008006f31" +
				"00000000000000000002bf2000010000000100000000000009f000000017000000000000000000000000000000170000000014ef177d",
				Convert.ToHexString(body).ToLowerInvariant());
		}

		[Fact]
		public void BuildDeathPose_ReproducesCapturedRecord_ByteExact()
		{
			// The minimal death-pose MoveTask (0x96C46740, movement-type 0x06) from AssassinL23_..._resurect pkt#4048,
			// dying player 0x08006F31 at the captured death position (big-endian floats; carrier prepends u32 len 0x1B).
			var pos = new Vector3(
				BitConverter.UInt32BitsToSingle(0x445C182F),
				BitConverter.UInt32BitsToSingle(0x433237B8),
				BitConverter.UInt32BitsToSingle(0x447E44D5));
			var body = WorldDeath.BuildDeathPose(0x08006F31, pos);
			Assert.Equal("96c467400000c35008006f310000445c182f433237b8447e44d506", Convert.ToHexString(body).ToLowerInvariant());
		}
	}
}
