using System;
using System.Numerics;
using Faolan.Core.Network.Codecs.World;
using Xunit;

namespace Faolan.Tests
{
	// Byte-exact construct + round-trip for the dead-reckoning movement records (#190, docs/re/chardcmove-grammar.md):
	// CharDCMoveIIR_t (0x4517AA81), StopIIR_c (0xD751EBB4), SetPosIIR_c (0x04B80B24). Oracles are captured bodies
	// (full record incl. the 4-byte length prefix). Proves the codecs are a faithful inverse of the wire grammar
	// (construct-don't-replay) across the flag-gated CharDCMove forms.
	public class DeadReckoningCodecTests
	{
		// hoxxling, bit4-set collapsed-facing form (flag1 0x1a): pos + velocity block.
		private const string CharDcMoveCollapsed = "000000394517aa810000c35001316e71011a438200004094d8aa43910000000000003f80000007000000004193aa3f0000000000000000000000000000";

		// bit4-clear form (flag1 0x0e): a full post-position move vector instead of the collapsed facing float.
		private const string CharDcMoveFullVector = "000000354517aa810000c350110099bd010e43b276e942a67267440c673bbd64ec683f469d8f3d8ebf563f1fe3d6003f8000004071ba5c0000";

		private const string SetPosBody = "0000002a04b80b240000c3500000aaf10044695fb0431f3d0444334865000000003e7b30d300000000bf782da300";

		// hoxxling stops at (294.12, 145.14, 726.28); the 4-float rotation is a unit 2D heading.
		private const string StopBody = "00000029d751ebb40000c35001316e710043930fe8431124a6443591dc00000000befe4aa4000000003f5e318d";

		[Fact]
		public void CharDCMove_BuildStationaryFacing_RoundTripsThroughParse()
		{
			var pos = new Vector3(260f, 4.65f, 290f);
			var body = CharDCMoveCodec.BuildStationaryFacing(0x01316E71, pos, Quaternion.Identity, 0.173f);

			var r = CharDCMoveCodec.Parse(body);
			Assert.Equal(0x01316E71u, r.Handle);
			Assert.Equal(0x1e, r.Flag1);
			Assert.True(r.Bit4);
			Assert.Equal(pos.X, r.Position.X, 3);
			Assert.Equal(Quaternion.Identity, r.Rotation);
			Assert.Equal(0.173f, r.Param19, 4);
		}

		// The floats after the position are the entity's rotation QUATERNION, not the decompile's "post-position move
		// vector" + facing scalars: bit4 SET collapses it to the two components a yaw-only facing needs (qy, qw), bit4
		// CLEAR carries all four. Both captured forms are unit-length - across the corpus that holds within 1e-4 for
		// 100% of 19,642 collapsed and 3,250 full records - and the yaw the collapsed pair implies matches the
		// direction of travel (within 7.5 deg for 90% of sustained straight runs), which is what pins it as an
		// orientation rather than a heading vector (a heading would be sin/cos, i.e. the FULL angle, not the half).
		[Theory, InlineData(CharDcMoveCollapsed, true), InlineData(CharDcMoveFullVector, false)]
		public void CharDCMove_RotationIsAUnitQuaternion(string hex, bool collapsed)
		{
			var r = CharDCMoveCodec.Parse(Convert.FromHexString(hex));

			Assert.Equal(collapsed, r.Bit4);
			Assert.Equal(1f, r.Rotation.Length(), 4);
			if (collapsed)
			{
				// The dropped components really are zero on the wire - that is why two floats suffice.
				Assert.Equal(0f, r.Rotation.X);
				Assert.Equal(0f, r.Rotation.Z);
			}
		}

		[Fact]
		public void CharDCMove_Parse_DecodesPositionAndFacing()
		{
			var r = CharDCMoveCodec.Parse(Convert.FromHexString(CharDcMoveCollapsed));
			Assert.Equal(0x01316E71u, r.Handle);
			Assert.True(r.Bit4); // collapsed facing
			Assert.Equal(260.0f, r.Position.X, 3);
			Assert.Equal(290.0f, r.Position.Z, 3);
		}

		[Theory, InlineData(CharDcMoveCollapsed), InlineData(CharDcMoveFullVector)]
		public void CharDCMove_RoundTripsByteExact(string hex)
		{
			var captured = Convert.FromHexString(hex);
			var rebuilt = CharDCMoveCodec.Build(CharDCMoveCodec.Parse(captured));
			Assert.Equal(hex, Convert.ToHexStringLower(rebuilt));
		}

		[Fact]
		public void SetPos_RoundTripsByteExact()
		{
			var captured = Convert.FromHexString(SetPosBody);
			var p = SetPosCodec.Parse(captured);
			var rebuilt = SetPosCodec.Build(p.Handle, p.Position, p.R0, p.R1, p.R2, p.R3, p.Flag, p.Env);
			Assert.Equal(SetPosBody, Convert.ToHexStringLower(rebuilt));
		}

		[Fact]
		public void Stop_Parse_DecodesPosition()
		{
			var p = StopCodec.Parse(Convert.FromHexString(StopBody));
			Assert.Equal(0x01316E71u, p.Handle);
			Assert.Equal(294.124f, p.Position.X, 2);
			Assert.Equal(726.279f, p.Position.Z, 2);
		}

		[Fact]
		public void Stop_RoundTripsByteExact()
		{
			var captured = Convert.FromHexString(StopBody);
			var p = StopCodec.Parse(captured);
			var rebuilt = StopCodec.Build(p.Handle, p.Position, p.R0, p.R1, p.R2, p.R3, p.Env);
			Assert.Equal(StopBody, Convert.ToHexStringLower(rebuilt));
		}
	}
}
