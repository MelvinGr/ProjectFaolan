using System.Collections.Generic;
using System.Numerics;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.World;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// Locks the minimal stationary MoveTask (96C46740) build: reconstructing it from the captured handle +
	// position reproduces the captured body byte-for-byte. This is the registration a non-patrolling NPC needs,
	// constructible from the spawn's own position (the full multi-segment patrol form is an enhancement).
	//
	// The captured goldens carry the carrier's leading [u32 len], which belongs to the SENDER (#530 rule 2), so the
	// codec's output is compared against the record past it.
	public class MoveTaskBuilderTests
	{
		[Fact]
		public void BuildPatrol_FromCapturedWaypoints_IsByteIdentical()
		{
			// Canonical single-segment waypoint patrol (handle 0x17E03, 2 waypoints). Reconstructing it from the
			// captured current position + waypoint path + speed reproduces the 96-byte body byte-for-byte.
			var captured = Golden.Bytes("MoveTaskPatrol");

			var handle = captured.ReadU32Be(12);
			var curPos = ReadVec3(captured, 18);
			int count = captured[40];
			var waypoints = new List<Vector3>();
			for (var k = 0; k < count; k++)
				waypoints.Add(ReadVec3(captured, 41 + 12 * k));
			var speed = captured.ReadF32Be(41 + 12 * count + 1);

			var built = MoveTaskCodec.Build(new MoveTaskIIR(handle, curPos, MoveTaskNodes.Patrol(waypoints, speed, curPos)));

			Assert.Equal(captured[4..], built);
		}

		[Fact]
		public void BuildPatrol_PairsWithSpawnHandleAndIsBigEndian()
		{
			var waypoints = new[] { new Vector3(100f, 10f, 200f), new Vector3(110f, 11f, 210f), new Vector3(120f, 12f, 220f) };
			var built = MoveTaskCodec.Build(new MoveTaskIIR(0x000900A1u, new Vector3(100f, 10f, 200f), MoveTaskNodes.Patrol(waypoints, 4.5f, new Vector3(100f, 10f, 200f))));

			Assert.Equal((uint)GameServer0X2000Opcodes.MoveTaskIIR, built.ReadU32Be(0));
			Assert.Equal(0x000900A1u, built.ReadU32Be(8));
			Assert.Equal((byte)0x04, built[26]); // patrol task type
			Assert.Equal((byte)waypoints.Length, built[36]);
			Assert.Equal(new byte[] { 0x00, 0x01 }, built[^2..]); // terminator
		}

		[Fact]
		public void BuildStationary_FromCapturedHandleAndPosition_IsByteIdentical()
		{
			var captured = Golden.Bytes("MoveTaskStationary");

			var handle = captured.ReadU32Be(12);
			var position = new Vector3(captured.ReadF32Be(18), captured.ReadF32Be(22),
				captured.ReadF32Be(26));

			var built = MoveTaskCodec.Build(new MoveTaskIIR(handle, position, MoveTaskNodes.Stationary()));

			Assert.Equal(captured[4..], built);
		}

		[Fact]
		public void BuildStationary_WritesHandleAndPositionBigEndian()
		{
			var built = MoveTaskCodec.Build(new MoveTaskIIR(0x000818D9u, new Vector3(461.22f, 162.71f, 664.95f), MoveTaskNodes.Stationary()));

			Assert.Equal(27, built.Length);
			Assert.Equal((uint)GameServer0X2000Opcodes.MoveTaskIIR, built.ReadU32Be(0));
			Assert.Equal(0x000818D9u, built.ReadU32Be(8));
			Assert.Equal(461.22f, built.ReadF32Be(14));
			Assert.Equal((byte)0x06, built[26]);
		}

		private static Vector3 ReadVec3(byte[] b, int off)
		{
			return new Vector3(b.ReadF32Be(off), b.ReadF32Be(off + 4),
				b.ReadF32Be(off + 8));
		}
	}
}
