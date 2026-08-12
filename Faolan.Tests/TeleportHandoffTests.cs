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
			var body = TeleportCodec.Build(0x08006F31, 2010);
			Assert.Equal("10d27bc00000c35008006f3101620000c79c000007da0000000000000000000000000000000000000000000000000000", Convert.ToHexStringLower(body));
		}

		[Fact]
		public void BuildTeleportBeginBody_MatchesSecondCapture()
		{
			// Frame 4417: same session, destination map 2000 (0x07D0).
			var body = TeleportCodec.Build(0x08006F31, 2000);
			Assert.Equal("10d27bc00000c35008006f3101620000c79c000007d00000000000000000000000000000000000000000000000000000", Convert.ToHexStringLower(body));
		}

		[Fact]
		public void BuildTeleportBeginBody_SubstitutesInstanceAndMap()
		{
			var body = TeleportCodec.Build(0x0200ABCD, 0x0FAA);
			// clientInstance @8, destMap @18 (both big-endian u32)
			Assert.Equal(0x0200ABCDu, body.ReadU32Be(8));
			Assert.Equal(0x0FAAu, body.ReadU32Be(18));
		}

		[Fact]
		public void BuildTeleportDestBody_MatchesCapture()
		{
			var body = TeleportCodec.Build(0x08006F31, 2010, new Vector3(928f, 116f, 870f));
			Assert.Equal(
				"5a32f0d70000c35008006f31000000000000000000000000000000000000000000000000003f800000620000c79c000007da0000000000000000000000000000000100009c500050357000000000000000000000000014000187044468000042e80000445980000000001400",
				Convert.ToHexStringLower(body));
		}

		[Fact]
		public void BuildTeleportDestBody_MatchesSecondCapture()
		{
			// Frame 4420: map 2000, pos 927/115/858, with that destination's playfield token (0x00503610) and
			// descriptor (0xBE) - the two destination-derived fields that differ from the map-2010 capture.
			var body = TeleportCodec.Build(0x08006F31, 2000, new Vector3(927f, 115f, 858f), 0x00503610u, 0x000000BEu);
			Assert.Equal(
				"5a32f0d70000c35008006f31000000000000000000000000000000000000000000000000003f800000620000c79c000007d00000000000000000000000000000000100009c500050361000000000000000000000000014000187044467c00042e6000044568000000000be00",
				Convert.ToHexStringLower(body));
		}

		[Fact]
		public void BuildTeleportDestBody_SubstitutesMapAndPosition()
		{
			var body = TeleportCodec.Build(0x0200ABCD, 0x0FAA, new Vector3(322.5f, 145.31f, 704.9f));
			Assert.Equal(0x0FAAu, body.ReadU32Be(46));
			Assert.Equal(322.5f, body.ReadF32Be(91));
			Assert.Equal(145.31f, body.ReadF32Be(95));
			Assert.Equal(704.9f, body.ReadF32Be(99));
		}

		// The SAME-MAP announce (#809). Stihetfv frame 1277, the beach resurrect on map 0xFAA: 88 bytes, an arrival
		// transform in the leading slot, an EMPTY spawn blob and trailing 1. This form had no golden, which is how our
		// builder came to emit 89 bytes - a second trailing byte the wire does not carry.
		[Fact]
		public void BuildSameMapDestBody_MatchesCapture()
		{
			var body = TeleportCodec.Build(0x021663DD, 0x0FAA, 0x021663DD, new Vector3(336.30899f, 148.372513f, 660.968018f),
				new Quaternion(0f, -0.9993636f, 0f, -0.035696145f));

			Assert.Equal(88, body.Length);
			Assert.Equal(
				"5a32f0d70000c350021663dd0043a8278d43145f5d44253df400000000bf7fd64b00000000bd12361f620000c7a000000faa00000000021663dd000000000000000000000000000000000000000000000000000000000001",
				Convert.ToHexStringLower(body));
		}

		// A PITCHED arrival - the shape the old "facingX, 0, facingZ" model could not express at all, and refused.
		// Hoxxling gameplay2 frame 9112: all four rotation words non-zero, and they normalise to 1.0.
		[Fact]
		public void ParseSameMapDestBody_ReadsTheRotationAsAQuaternion()
		{
			var captured = Convert.FromHexString(
				"5a32f0d70000c3500802e5d400437f999a431a654f43d48ccdbc2b72cd3e04265c3ab222c53f7dd84b620000c7a0000003fc0000000000000000000000000000000000000000000000000000000000000000000000000001");

			var parsed = Assert.IsType<N3TeleportIIR>(TeleportCodec.Parse(captured));

			Assert.Null(parsed.Spawn); // no blob: the arrival is the leading transform
			Assert.Equal(1f, parsed.Rotation.Length(), 4);
			Assert.Equal(parsed.Position, parsed.ArrivalPosition);
			Assert.Equal((byte)0x01, parsed.Trailing);
			Assert.Equal(Convert.ToHexStringLower(captured), Convert.ToHexStringLower(TeleportCodec.Build(parsed)));
		}
	}
}
