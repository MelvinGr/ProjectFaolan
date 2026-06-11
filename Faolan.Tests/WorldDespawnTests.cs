using Faolan.GameServer.World;
using Xunit;

namespace Faolan.Tests
{
	// The despawn packet (n3ToClientQuit 0x04B82B2D) and its playfield-token extraction
	// (docs/server-architecture-roadmap.md, protocol_despawn_n3toclientquit). Body decoded from retail captures:
	// [04B82B2D][0000C350][handle][0x01 marker][u32 playfield token].
	public class WorldDespawnTests
	{
		[Fact]
		public void BuildBody_MatchesRetailLayout()
		{
			// The hoxxling gameplay capture: removing handle 0x00017E29 in playfield 0x01EA985C produced this body.
			var body = WorldDespawn.BuildBody(0x00017E29, 0x01EA985C);

			Assert.Equal(17, body.Length);
			var expected = new byte[]
			{
				0x04, 0xB8, 0x2B, 0x2D, // subop
				0x00, 0x00, 0xC3, 0x50, // typeTag/ack
				0x00, 0x01, 0x7E, 0x29, // handle
				0x01, // marker
				0x01, 0xEA, 0x98, 0x5C // playfield token
			};
			Assert.Equal(expected, body);
		}

		[Fact]
		public void TryExtractPlayfieldToken_FindsTokenBetweenMarkers()
		{
			// ...000000BF <token> 0000040B - the framing seen in the beach burst (token 0x004FE428).
			var snapshot = new byte[]
			{
				0xDE, 0xAD, // some preceding tail bytes
				0x00, 0x00, 0x00, 0xBF, // prefix marker
				0x00, 0x4F, 0xE4, 0x28, // token
				0x00, 0x00, 0x04, 0x0B, // suffix marker
				0xBE, 0xEF
			};

			Assert.True(WorldDespawn.TryExtractPlayfieldToken(snapshot, out var token));
			Assert.Equal(0x004FE428u, token);
		}

		[Fact]
		public void TryExtractPlayfieldToken_ReturnsFalseWhenAbsent()
		{
			Assert.False(WorldDespawn.TryExtractPlayfieldToken(new byte[] { 0x01, 0x02, 0x03, 0x04 }, out var token));
			Assert.Equal(0u, token);
		}

		[Fact]
		public void TryExtractPlayfieldToken_SkipsPrefixWithoutSuffix()
		{
			// A 000000BF that is NOT followed by the 0000040B suffix must be skipped, not mistaken for the token frame.
			var snapshot = new byte[]
			{
				0x00, 0x00, 0x00, 0xBF, // decoy prefix
				0x11, 0x22, 0x33, 0x44, // not the token
				0x55, 0x66, 0x77, 0x88, // not the suffix
				0x00, 0x00, 0x00, 0xBF, // real prefix
				0x01, 0xEA, 0x98, 0x5C, // token
				0x00, 0x00, 0x04, 0x0B // suffix
			};

			Assert.True(WorldDespawn.TryExtractPlayfieldToken(snapshot, out var token));
			Assert.Equal(0x01EA985Cu, token);
		}
	}
}
