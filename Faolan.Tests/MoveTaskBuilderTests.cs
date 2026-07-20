using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// Locks the minimal stationary MoveTask (96C46740) build: reconstructing it from the captured handle +
	// position reproduces the captured 31-byte body byte-for-byte. This is the registration a non-patrolling NPC
	// needs, constructible from the spawn's own position (the full multi-segment patrol form is an enhancement).
	public class MoveTaskBuilderTests
	{
		[Fact]
		public void BuildPatrol_FromCapturedWaypoints_IsByteIdentical()
		{
			// Canonical single-segment waypoint patrol (handle 0x17E03, 2 waypoints). Reconstructing it from the
			// captured current position + waypoint path + speed reproduces the 96-byte body byte-for-byte.
			var captured = Golden.Bytes("MoveTaskPatrol");

			var handle = BinaryPrimitives.ReadUInt32BigEndian(captured.AsSpan(12, 4));
			var curPos = ReadVec3(captured, 18);
			int count = captured[40];
			var waypoints = new List<Vector3>();
			for (var k = 0; k < count; k++)
				waypoints.Add(ReadVec3(captured, 41 + 12 * k));
			var speed = BinaryPrimitives.ReadSingleBigEndian(captured.AsSpan(41 + 12 * count + 1, 4));

			var built = MoveTaskCodec.BuildPatrol(handle, curPos, waypoints, speed);

			Assert.Equal(captured, built);
		}

		[Fact]
		public void BuildPatrol_PairsWithSpawnHandleAndIsBigEndian()
		{
			var waypoints = new[] { new Vector3(100f, 10f, 200f), new Vector3(110f, 11f, 210f), new Vector3(120f, 12f, 220f) };
			var built = MoveTaskCodec.BuildPatrol(0x000900A1u, new Vector3(100f, 10f, 200f), waypoints, 4.5f);

			Assert.Equal((uint)GameServer0X2000Opcodes.MoveTaskIIR, BinaryPrimitives.ReadUInt32BigEndian(built.AsSpan(4, 4)));
			Assert.Equal(0x000900A1u, BinaryPrimitives.ReadUInt32BigEndian(built.AsSpan(12, 4)));
			Assert.Equal((byte)0x04, built[30]); // patrol task type
			Assert.Equal((byte)waypoints.Length, built[40]);
			Assert.Equal((uint)(built.Length - 4), BinaryPrimitives.ReadUInt32BigEndian(built.AsSpan(0, 4)));
			Assert.Equal(new byte[] { 0x00, 0x01 }, built[^2..]); // terminator
		}

		[Fact]
		public void BuildStationary_FromCapturedHandleAndPosition_IsByteIdentical()
		{
			var captured = Golden.Bytes("MoveTaskStationary");

			var handle = BinaryPrimitives.ReadUInt32BigEndian(captured.AsSpan(12, 4));
			var position = new Vector3(BinaryPrimitives.ReadSingleBigEndian(captured.AsSpan(18, 4)), BinaryPrimitives.ReadSingleBigEndian(captured.AsSpan(22, 4)),
				BinaryPrimitives.ReadSingleBigEndian(captured.AsSpan(26, 4)));

			var built = MoveTaskCodec.BuildStationary(handle, position);

			Assert.Equal(captured, built);
		}

		[Fact]
		public void BuildStationary_WritesHandleAndPositionBigEndian()
		{
			var built = MoveTaskCodec.BuildStationary(0x000818D9u, new Vector3(461.22f, 162.71f, 664.95f));

			Assert.Equal(31, built.Length);
			Assert.Equal((uint)GameServer0X2000Opcodes.MoveTaskIIR, BinaryPrimitives.ReadUInt32BigEndian(built.AsSpan(4, 4)));
			Assert.Equal(0x000818D9u, BinaryPrimitives.ReadUInt32BigEndian(built.AsSpan(12, 4)));
			Assert.Equal(461.22f, BinaryPrimitives.ReadSingleBigEndian(built.AsSpan(18, 4)));
			Assert.Equal((byte)0x06, built[30]);
		}

		private static Vector3 ReadVec3(byte[] b, int off)
		{
			return new Vector3(BinaryPrimitives.ReadSingleBigEndian(b.AsSpan(off, 4)), BinaryPrimitives.ReadSingleBigEndian(b.AsSpan(off + 4, 4)),
				BinaryPrimitives.ReadSingleBigEndian(b.AsSpan(off + 8, 4)));
		}
	}
}
