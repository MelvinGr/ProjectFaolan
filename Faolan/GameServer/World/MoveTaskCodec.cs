using System.Collections.Generic;
using System.Numerics;
using Faolan.Core.Data;
using Faolan.Core.Network;
using Faolan.Core.Network.Packets.Opcodes;

namespace Faolan.GameServer.World
{
	// The MoveTask transform packet (MoveTaskIIR 0x96C46740) - how the server tells clients where an entity is and which
	// way it faces. The full recursive grammar is decoded (100% round-trip, docs/movetask-grammar.md); this codec builds
	// the forms we emit: the 61-byte "at-position-facing" form (the simplest + by far the most common, used by the player
	// too), the bare-position + approach-target forms, and the multi-waypoint PathMoveTask (BuildPathBody). The 61-byte
	// form's two positions are a FROM/TO segment - in the capture's stationary updates from==to; for a moving NPC we send
	// from = the previous position, to = the stepped position so the client interpolates the step.
	//
	// Wire body (the inner 0x2000 IIR record, after the u32 totalLen WriteArrayUInt32Length wraps), all big-endian:
	//   [subop 0x96C46740][0x0000C350][handle][u16 0][from: 3 floats][u8 0x02][to: 3 floats][quat: 4 floats][u16 0]
	public static class MoveTaskCodec
	{
		public const uint MoveTaskSubOpcode = (uint)GameServer0X2000RespondsOpcodes.MoveTaskIIR;
		private const uint WireType = EntityClass.Character; // the shared 0xC350 type word
		public const int BodyLength = 57;

		// The MoveTask "mode" byte (at body offset 26, after the from-position) selects the form (decoded from the
		// captures, #53): 0x00 = a bare position, 0x02 = the stationary from/to/facing below, 0x04 = a multi-waypoint
		// path (the PatrolPath source, still variable-length), 0x08 = approach/face a target entity.
		public const byte ModeBarePosition = 0x00;
		public const byte ModePath = 0x01;
		public const byte ModeStationary = 0x02;
		public const byte ModeApproachTarget = 0x08;

		// mode-00: a bare position update - [u16 0][from 3 floats][0x00][0x01] (28-byte body / 32-byte frame). The
		// simplest move; the dominant shape for the player's own position ticks. Validated 74/74.
		public static byte[] BuildBarePositionBody(uint handle, Vector3 from)
		{
			return new FaolanStream()
				.WriteUInt32(MoveTaskSubOpcode)
				.WriteUInt32(WireType)
				.WriteUInt32(handle)
				.WriteUInt16(0)
				.WriteFloatBigEndian(from.X).WriteFloatBigEndian(from.Y).WriteFloatBigEndian(from.Z)
				.WriteByte(ModeBarePosition)
				.WriteByte(0x01)
				.ToArray();
		}

		// mode-08: approach/face a target entity - [u16 0][from 3 floats][0x08][u32 0xC350 type][u32 targetHandle]
		// [0x01][9 zero bytes] (45-byte body / 49-byte frame). The NPC "turn to / move toward" a dynel. Validated 825/825.
		public static byte[] BuildApproachTargetBody(uint handle, Vector3 from, uint targetHandle)
		{
			return new FaolanStream()
				.WriteUInt32(MoveTaskSubOpcode)
				.WriteUInt32(WireType)
				.WriteUInt32(handle)
				.WriteUInt16(0)
				.WriteFloatBigEndian(from.X).WriteFloatBigEndian(from.Y).WriteFloatBigEndian(from.Z)
				.WriteByte(ModeApproachTarget)
				.WriteUInt32(WireType)
				.WriteUInt32(targetHandle)
				.WriteByte(0x01)
				.WriteUInt64(0)
				.WriteByte(0x00)
				.ToArray();
		}

		// mode-01 PathMoveTask: the multi-waypoint patrol path - decoded from the captures + the client reader
		// FUN_00a483b0 (docs/movetask-grammar.md, #53). Canonical shape (92/92 captured else-branch records): the
		// WaitMoveTask base header with flags=0x01 (bit0 set -> no duration/remaining floats) + the PathMoveTask
		// F-flags=0x00 (no optional floats), then [u32 count][count x position] and a fixed 29-byte trailer
		//   [0x00][f32 pathLength][12x 00][from x/y/z].
		// `pathLength` is the from -> last-waypoint distance (ratio 1.000 across the captures); it is a PARAMETER
		// because the retail value differs from a recomputed float by ~1 ULP - the overload computes it for a live
		// path (the client uses it for interpolation timing, where the last-ULP difference is irrelevant).
		public static byte[] BuildPathBody(uint handle, Vector3 from, IReadOnlyList<Vector3> waypoints, float pathLength)
		{
			var s = new FaolanStream()
				.WriteUInt32(MoveTaskSubOpcode)
				.WriteUInt32(WireType)
				.WriteUInt32(handle)
				.WriteUInt16(0)
				.WriteFloatBigEndian(from.X).WriteFloatBigEndian(from.Y).WriteFloatBigEndian(from.Z)
				.WriteByte(ModePath)
				.WriteByte(0x01)                     // WaitMoveTask base header flags (bit0 -> no dur/rem floats)
				.WriteByte(0x00)                     // PathMoveTask F-flags (no optional floats; waypoint-list branch)
				.WriteUInt32((uint)waypoints.Count);
			foreach (var w in waypoints)
				s.WriteFloatBigEndian(w.X).WriteFloatBigEndian(w.Y).WriteFloatBigEndian(w.Z);
			return s
				.WriteByte(0x00)                     // trailer byte
				.WriteFloatBigEndian(pathLength)
				.WriteUInt64(0).WriteUInt32(0)       // 12 zero bytes
				.WriteFloatBigEndian(from.X).WriteFloatBigEndian(from.Y).WriteFloatBigEndian(from.Z)
				.ToArray();
		}

		// Live overload: the trailer path-length is the TOTAL distance walked (from -> wp0 -> wp1 -> ... -> wpN), the
		// sum of the segments. The client uses it for interpolation TIMING - a too-small value (e.g. the straight-line
		// from->last) makes a winding patrol RUN to cover the real path in the short time, so a multi-waypoint loop
		// must report its full traversal length to walk at the right pace. (For a straight path the sum equals
		// from->last, matching the captured ratio-1.000 records; #266.)
		public static byte[] BuildPathBody(uint handle, Vector3 from, IReadOnlyList<Vector3> waypoints)
		{
			return BuildPathBody(handle, from, waypoints, TotalPathLength(from, waypoints));
		}

		// Sum of the segment distances along from -> waypoints (the real walked length).
		public static float TotalPathLength(Vector3 from, IReadOnlyList<Vector3> waypoints)
		{
			if (waypoints == null || waypoints.Count == 0)
				return 0f;
			var length = Vector3.Distance(from, waypoints[0]);
			for (var i = 1; i < waypoints.Count; i++)
				length += Vector3.Distance(waypoints[i - 1], waypoints[i]);
			return length;
		}

		// Builds the 61-byte-frame MoveTask body for `handle` moving from `from` to `to`, facing `facing`. from==to is a
		// stationary orientation/position update (the capture's shape); from!=to is a step the client walks along.
		public static byte[] BuildBody(uint handle, Vector3 from, Vector3 to, Quaternion facing)
		{
			return new FaolanStream()
				.WriteUInt32(MoveTaskSubOpcode)
				.WriteUInt32(WireType)
				.WriteUInt32(handle)
				.WriteUInt16(0)
				.WriteFloatBigEndian(from.X).WriteFloatBigEndian(from.Y).WriteFloatBigEndian(from.Z)
				.WriteByte(0x02)
				.WriteFloatBigEndian(to.X).WriteFloatBigEndian(to.Y).WriteFloatBigEndian(to.Z)
				.WriteFloatBigEndian(facing.X).WriteFloatBigEndian(facing.Y).WriteFloatBigEndian(facing.Z).WriteFloatBigEndian(facing.W)
				.WriteUInt16(0)
				.ToArray();
		}
	}
}
