using System;
using System.Numerics;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.World;
using Xunit;

namespace Faolan.Tests
{
	// The change-map prep packets (0x10D27BC0 begin + 0x5A32F0D7 destination announce) the retail teleport sends
	// before the 0x2002 handoff, decoded from the changemap captures (frames 4200/4212, consistent across both
	// changemap pcaps). Locks the substitution offsets/endianness by reproducing the captured bytes exactly with
	// the captured session values (instance 0x08006F31, map 2010=0x07DA, pos 928/116/870).
	public class TeleportHandoffTests
	{
		[Fact]
		public void BuildTeleportBeginBody_MatchesCapture()
		{
			var body = TeleportCodec.BuildBegin(0x08006F31, 2010);
			Assert.Equal("10d27bc00000c35008006f3101620000c79c000007da0000000000000000000000000000000000000000000000000000", Convert.ToHexStringLower(body));
		}

		[Fact]
		public void BuildTeleportBeginBody_MatchesSecondCapture()
		{
			// Frame 4417: same session, destination map 2000 (0x07D0).
			var body = TeleportCodec.BuildBegin(0x08006F31, 2000);
			Assert.Equal("10d27bc00000c35008006f3101620000c79c000007d00000000000000000000000000000000000000000000000000000", Convert.ToHexStringLower(body));
		}

		[Fact]
		public void BuildTeleportBeginBody_SubstitutesInstanceAndMap()
		{
			var body = TeleportCodec.BuildBegin(0x0200ABCD, 0x0FAA);
			// clientInstance @8, destMap @18 (both big-endian u32)
			Assert.Equal(0x0200ABCDu, body.ReadU32Be(8));
			Assert.Equal(0x0FAAu, body.ReadU32Be(18));
		}

		[Fact]
		public void BuildTeleportDestBody_MatchesCapture()
		{
			var body = TeleportCodec.BuildDestination(0x08006F31, 2010, new Vector3(928f, 116f, 870f));
			Assert.Equal(
				"5a32f0d70000c35008006f31000000000000000000000000000000000000000000000000003f800000620000c79c000007da0000000000000000000000000000000100009c500050357000000000000000000000000014000187044468000042e80000445980000000001400",
				Convert.ToHexStringLower(body));
		}

		[Fact]
		public void BuildTeleportDestBody_MatchesSecondCapture()
		{
			// Frame 4420: map 2000, pos 927/115/858, with that destination's playfield token (0x00503610) and
			// descriptor (0xBE) - the two destination-derived fields that differ from the map-2010 capture.
			var body = TeleportCodec.BuildDestination(0x08006F31, 2000, new Vector3(927f, 115f, 858f), 0x00503610u, 0x000000BEu);
			Assert.Equal(
				"5a32f0d70000c35008006f31000000000000000000000000000000000000000000000000003f800000620000c79c000007d00000000000000000000000000000000100009c500050361000000000000000000000000014000187044467c00042e6000044568000000000be00",
				Convert.ToHexStringLower(body));
		}

		[Fact]
		public void BuildTeleportDestBody_SubstitutesMapAndPosition()
		{
			var body = TeleportCodec.BuildDestination(0x0200ABCD, 0x0FAA, new Vector3(322.5f, 145.31f, 704.9f));
			Assert.Equal(0x0FAAu, body.ReadU32Be(46));
			Assert.Equal(322.5f, body.ReadF32Be(91));
			Assert.Equal(145.31f, body.ReadF32Be(95));
			Assert.Equal(704.9f, body.ReadF32Be(99));
		}
	}
}
