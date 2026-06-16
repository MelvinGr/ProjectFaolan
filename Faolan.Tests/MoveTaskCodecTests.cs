using System;
using System.Numerics;
using Faolan.Core.Extensions;
using Faolan.GameServer.World;
using Xunit;

namespace Faolan.Tests
{
	// Golden test for the 61-byte-frame MoveTask (MoveTaskIIR 0x96C46740) - the "at-position-facing" transform form,
	// byte-exact against frame 2480 of the strickenbla combat capture (actor 0x0002324d, stationary, facing -90deg about
	// Y). The expected hex is the captured 0x2000 body MINUS its 4-byte totalLen prefix (the builder produces the inner
	// record that WriteArrayUInt32Length wraps).
	public class MoveTaskCodecTests
	{
		private static float Bits(uint v) => BitConverter.Int32BitsToSingle(unchecked((int)v));

		[Fact]
		public void MoveTaskBody_MatchesCapturedFrame2480()
		{
			// pos1 == pos2 (stationary); quat = (0, -0.707.., 0, 0.707..) about Y.
			var pos = new Vector3(Bits(0x4385599A), Bits(0x40BD9169), Bits(0x4392CCCD));
			var quat = new Quaternion(Bits(0x00000000), Bits(0xBF3504F2), Bits(0x00000000), Bits(0x3F3504F4));

			var body = MoveTaskCodec.BuildBody(0x0002324D, pos, pos, quat);

			Assert.Equal(MoveTaskCodec.BodyLength, body.Length);
			Assert.Equal(
				"96c467400000c3500002324d00004385599a40bd91694392cccd024385599a40bd91694392cccd00000000bf3504f2000000003f3504f40000",
				Convert.ToHexStringLower(body));
		}

		// mode-00: a bare position update [from][00][01], byte-exact against a captured player position tick (#53).
		[Fact]
		public void BarePositionBody_MatchesCapture()
		{
			var from = new Vector3(Bits(0x43930FE8), Bits(0x431124A6), Bits(0x443591DC));

			var body = MoveTaskCodec.BuildBarePositionBody(0x01316E71, from);

			Assert.Equal("96c467400000c35001316e71000043930fe8431124a6443591dc0001", Convert.ToHexStringLower(body));
		}

		// mode-08: approach/face a target entity [from][08][c350][targetHandle][01][9 zeros], byte-exact vs capture (#53).
		[Fact]
		public void ApproachTargetBody_MatchesCapture()
		{
			var from = new Vector3(Bits(0x43A51ACD), Bits(0x43115C2A), Bits(0x442A2D1A));

			var body = MoveTaskCodec.BuildApproachTargetBody(0x00017F80, from, targetHandle: 0x01316E71);

			Assert.Equal("96c467400000c35000017f80000043a51acd43115c2a442a2d1a080000c35001316e7101000000000000000000", Convert.ToHexStringLower(body));
		}

		[Fact]
		public void FaceTowards_YawsAboutY_TowardTarget()
		{
			var at = new Vector3(0, 0, 0);
			// target straight along +X => yaw = atan2(1,0) = 90deg => quat (0, sin45, 0, cos45).
			var q = at.FaceTowards(new Vector3(10, 0, 0));
			Assert.Equal(0f, q.X, 5);
			Assert.Equal(MathF.Sqrt(2) / 2, q.Y, 4);
			Assert.Equal(0f, q.Z, 5);
			Assert.Equal(MathF.Sqrt(2) / 2, q.W, 4);

			// coincident points => identity (no facing change).
			Assert.Equal(Quaternion.Identity, at.FaceTowards(at));
		}
	}
}
