using System;
using System.Collections.Generic;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Protobuf;
using Xunit;

namespace Faolan.Tests
{
	// Locks the SpawnPlayer position/rotation migration: the generated ProtoVec3 / ProtoQuat must serialize to the
	// exact bytes the packet used to hand-roll - protobuf fixed32 tags (0x0D/0x15/0x1D/0x25) followed by each
	// component as a little-endian IEEE-754 float (FaolanStream.WriteFloat is little-endian). If this drifts, the
	// SpawnPlayer 0x2000 packet would change shape and the client could reject the spawn.
	public class SpawnTransformGoldenTests
	{
		[Theory, InlineData(0f, 0f, 0f, 1f), InlineData(0.5f, -0.5f, 0.25f, -0.75f)]
		public void ProtoQuat_MatchesHandRolledFixed32(float x, float y, float z, float w)
		{
			var bytes = new ProtoQuat
			{
				X = x,
				Y = y,
				Z = z,
				W = w
			}.ToProtobufBytes();

			Assert.Equal(20, bytes.Length); // 4 * 5 - the old 0x14 length prefix
			Assert.Equal(Convert.ToHexString(HandRolledQuat(x, y, z, w)), Convert.ToHexString(bytes));
		}

		[Theory, InlineData(0f, 0f, 0f), InlineData(-1234.5f, 0.001f, 98765.4f)]
		public void ProtoVec3_MatchesHandRolledFixed32(float x, float y, float z)
		{
			Assert.True(BitConverter.IsLittleEndian, "test assumes a little-endian host");

			var bytes = new ProtoVec3
			{
				X = x,
				Y = y,
				Z = z
			}.ToProtobufBytes();

			Assert.Equal(15, bytes.Length); // 3 * (1 tag + 4 float bytes) - the old 0x0F length prefix
			Assert.Equal(Convert.ToHexString(HandRolledVec3(x, y, z)), Convert.ToHexString(bytes));
		}

		private static byte[] HandRolledQuat(float x, float y, float z, float w)
		{
			var b = new List<byte>(HandRolledVec3(x, y, z)) { 0x25 };
			b.AddRange(BitConverter.GetBytes(w));
			return b.ToArray();
		}

		private static byte[] HandRolledVec3(float x, float y, float z)
		{
			var b = new List<byte> { 0x0D };
			b.AddRange(BitConverter.GetBytes(x));
			b.Add(0x15);
			b.AddRange(BitConverter.GetBytes(y));
			b.Add(0x1D);
			b.AddRange(BitConverter.GetBytes(z));
			return b.ToArray();
		}
	}
}
