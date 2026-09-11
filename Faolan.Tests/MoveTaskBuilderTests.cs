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
	// The captured goldens carry the carrier's leading [u32 len], which belongs to the sender, so the codec's output
	// is compared against the record past it.
	public class MoveTaskBuilderTests
	{
		// The two captured MoveTask records, byte-for-byte past the carrier's [u32 len], pinned as SHA-256 digests:
		// the retail blobs themselves are not carried in the repo.
		//
		// The inputs are spelled out as literals rather than read back out of a golden at fixed offsets - that says
		// what the capture holds, and the digest still says the builder reproduces it.
		private const string CapturedPatrolSha256 = "2e659474d4b316fd50b6978ad9f7436540a63957875b09bef45c07b12edd174c";
		private const string CapturedStationarySha256 = "46654fd27c528366b102b2895147fc97ef4d0964be4820f15e50da46b3602f0a";

		// Formation-follow (mode 0x07), the wire form of the ambient NPC-vs-NPC chase. In
		// NecroL80_..._annotated_1 the maintainer annotates "giner & scruffy should be chasing each other", and the two
		// NPCs he means are `Ginger` 0x31C7A - which gets a 694-byte mode-04 patrol with 52 waypoints - and 0x31CFD,
		// which gets this: a 55-byte mode-07 naming 0x31C7A as its target at an offset of -4.5. The pair is re-issued
		// together every ~15 s.
		//
		// So the chase is an ordinary MoveTask retargeted at the other NPC's handle, not a scripted behaviour. The
		// codec already parsed mode 7 (it round-trips over the corpus); what it had no way to do was build one.
		[Fact]
		public void BuildFormation_IsByteIdenticalToTheCapturedChase()
		{
			// subop, class, follower, u16 0, its position | mode 0x07 | the leader's class + handle |
			// the formation offset (0, 0, -4.5) and slot 0 - Scruffy holds 4.5 units directly behind Ginger.
			const string captured = "96c467400000c35000031cfd000044548e5b42cafa5f43fe15c9" +
			                        "07" +
			                        "0000c35000031c7a" +
			                        "0000000000000000c090000000000000";

			// The position words verbatim, rather than decimal literals - a float printed to 7 digits and re-parsed
			// does not always land on the same bits, and this gate is about the bits.
			var built = MoveTaskCodec.Build(new MoveTaskIIR(0x00031CFD, 0x44548E5B, 0x42CAFA5F, 0x43FE15C9,
				MoveTaskNodes.Formation(0x00031C7A, new Vector3(0f, 0f, -4.5f))));

			Assert.Equal(captured, Convert.ToHexStringLower(built));
		}

		[Fact]
		public void BuildPatrol_FromCapturedWaypoints_IsByteIdentical()
		{
			// Canonical single-segment waypoint patrol (handle 0x17E03, 2 waypoints). Reconstructing it from the
			// captured current position + waypoint path + speed reproduces the 92-byte record byte-for-byte.
			var curPos = new Vector3(333.2f, 146.29666f, 706.026f);
			var waypoints = new List<Vector3>
			{
				curPos,
				new Vector3(324.76794f, 145.43733f, 705.13867f)
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
		// The companion-follow step, against Casilda's follow in
		// Hoxxling_mage-heraldofxotli_login_gameplay frame 1959 (her handle 0x00017ED0 walking behind the player
		// 0x01316E71). A bare mode-01 path is interpolated by the client, which then sits at the end of it - so the
		// server has to re-anchor every ~6u and the follower snaps between segments instead of walking.
		//
		// Retail sends a mode-04 list of two: the path segment, then a mode-08 node bound to the leader's handle.
		// That second child is what hands the follow to the client, so it owns the position and the walk animation
		// together - the same reason a patrol animates correctly.
		//
		// Every byte matches except the four-byte trailer scalar, excluded deliberately and not papered over.
		// It is the segment's own 3D length; retail's is 0x40E21A9E and every IEEE route gives 0x40E21A9F - float32
		// accumulation and double accumulation alike. One ulp, from the client's x87 80-bit intermediates, which
		// managed .Net cannot reproduce and should not try to.
		//
		// And it does not matter, which Ghidra settles rather than assumption: the trailer is read by
		// `FUN_00a3e400` into a struct at +4..+0x20, and the client's movement speed does not come from it at all -
		// `FUN_00a2e670` switches on the spawn's mode and reads a per-entity field per mode (+0x13c/+0x134 for 1|4,
		// +0x140 for 2|5). The only MoveTask field that touches speed is the `PathFlags & 0x20` float at +0x60, a
		// max-speed cap the executor applies as `if (cap < speed) speed = cap` before advancing by `speed * dt` -
		// and retail's follow record does not set it (PathFlags 0).
		[Fact]
		public void CasildaFollowMoveTask_MatchesCapture()
		{
			const string retail = "96c467400000c35000017ed0000043b4d24e4319572244336ac1040100020101000000000243b4d24e4319572244336ac1" +
			                      "43b81c9f431a98c84433fa760040e21a9e00000000000000000000000043b4d24e4319572244336ac1" +
			                      "080000c35001316e71013f490fdb3f490fdb00";

			// [subop|class|handle|u16|from] 26 + list head 4 + path head 7 + 24 waypoint bytes + 1 trailer byte.
			const int scalarOffset = 62;

			var from = new Vector3(361.64300537109375f, 153.34036254882813f, 717.6680297851563f);
			var to = new Vector3(368.2236022949219f, 154.5968017578125f, 719.9134521484375f);

			var actual = Convert.ToHexString(MoveTaskCodec.Build(new MoveTaskIIR(0x00017ED0, from, MoveTaskNodes.FollowLeader(from, to, 0x01316E71)))).ToLowerInvariant();

			Assert.Equal(retail.Length, actual.Length);
			Assert.Equal(retail[..(scalarOffset * 2)], actual[..(scalarOffset * 2)]);
			Assert.Equal(retail[((scalarOffset + 4) * 2)..], actual[((scalarOffset + 4) * 2)..]);

			var ours = BitConverter.UInt32BitsToSingle(Convert.ToUInt32(actual.Substring(scalarOffset * 2, 8), 16));
			var theirs = BitConverter.UInt32BitsToSingle(Convert.ToUInt32(retail.Substring(scalarOffset * 2, 8), 16));
			Assert.Equal(theirs, ours, 4);
		}

	}
}
