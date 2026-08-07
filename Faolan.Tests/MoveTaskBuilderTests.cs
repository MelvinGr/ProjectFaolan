using System;
using System.Collections.Generic;
using System.Numerics;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.World;
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
		// The two captured MoveTask records, byte-for-byte, past the carrier's [u32 len]. Verified against
		// Golden/MoveTaskPatrol.bin and Golden/MoveTaskStationary.bin (since deleted, #555) before removal.
		//
		// The INPUTS moved into the tests as literals when the blobs went, which is the honest form of what the old
		// version was doing anyway: it read the handle, positions, waypoints and speed back out of the golden at
		// fixed offsets and fed them to the builder. Spelling them out says what the capture holds; the digest still
		// says the builder reproduces it.
		private const string CapturedPatrolSha256 = "2e659474d4b316fd50b6978ad9f7436540a63957875b09bef45c07b12edd174c";
		private const string CapturedStationarySha256 = "46654fd27c528366b102b2895147fc97ef4d0964be4820f15e50da46b3602f0a";

		[Fact]
		public void BuildPatrol_FromCapturedWaypoints_IsByteIdentical()
		{
			// Canonical single-segment waypoint patrol (handle 0x17E03, 2 waypoints). Reconstructing it from the
			// captured current position + waypoint path + speed reproduces the 92-byte record byte-for-byte.
			var curPos = new Vector3(333.2f, 146.29666f, 706.026f);
			var waypoints = new List<Vector3>
			{
				curPos,
				new(324.76794f, 145.43733f, 705.13867f)
			};

			var built = MoveTaskCodec.Build(new MoveTaskIIR(0x00017E03u, curPos, MoveTaskNodes.Patrol(waypoints, 8.522063f, curPos)));

			Assert.Equal(92, built.Length);
			Assert.Equal(CapturedPatrolSha256, Sha256(built));
		}

		[Fact]
		public void BuildPatrol_PairsWithSpawnHandleAndIsBigEndian()
		{
			var waypoints = new[] { new Vector3(100f, 10f, 200f), new Vector3(110f, 11f, 210f), new Vector3(120f, 12f, 220f) };
			var built = MoveTaskCodec.Build(new MoveTaskIIR(0x000900A1u, new Vector3(100f, 10f, 200f), MoveTaskNodes.Patrol(waypoints, 4.5f, new Vector3(100f, 10f, 200f))));

			Assert.Equal((uint)IirTypes.MoveTaskIIR, built.ReadU32Be(0));
			Assert.Equal(0x000900A1u, built.ReadU32Be(8));
			Assert.Equal((byte)0x04, built[26]); // patrol task type
			Assert.Equal((byte)waypoints.Length, built[36]);
			Assert.Equal(new byte[] { 0x00, 0x01 }, built[^2..]); // terminator
		}

		[Fact]
		public void BuildStationary_FromCapturedHandleAndPosition_IsByteIdentical()
		{
			var built = MoveTaskCodec.Build(new MoveTaskIIR(0x0000AA39u, new Vector3(921.9175f, 171.4333f, 847.75623f), MoveTaskNodes.Stationary()));

			Assert.Equal(27, built.Length);
			Assert.Equal(CapturedStationarySha256, Sha256(built));
		}

		[Fact]
		public void BuildStationary_WritesHandleAndPositionBigEndian()
		{
			var built = MoveTaskCodec.Build(new MoveTaskIIR(0x000818D9u, new Vector3(461.22f, 162.71f, 664.95f), MoveTaskNodes.Stationary()));

			Assert.Equal(27, built.Length);
			Assert.Equal((uint)IirTypes.MoveTaskIIR, built.ReadU32Be(0));
			Assert.Equal(0x000818D9u, built.ReadU32Be(8));
			Assert.Equal(461.22f, built.ReadF32Be(14));
			Assert.Equal((byte)0x06, built[26]);
		}

		private static string Sha256(byte[] body)
		{
			return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(body));
		}
	}
}
