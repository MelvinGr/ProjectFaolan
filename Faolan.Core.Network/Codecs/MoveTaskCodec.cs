using System.Collections.Generic;
using System.Numerics;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Opcodes;

namespace Faolan.Core.Network.Codecs
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
		public const byte ModeApproachTarget = 0x08;

		// The MoveTask "mode" byte (at body offset 26, after the from-position) selects the form (decoded from the
		// captures, #53): 0x00 = a bare position, 0x02 = the stationary from/to/facing below, 0x04 = a multi-waypoint
		// path (the PatrolPath source, still variable-length), 0x08 = approach/face a target entity.
		public const byte ModeBarePosition = 0x00;
		public const byte ModePath = 0x01;
		public const byte ModeStationary = 0x02;
		public const int BodyLength = 57;

		// mode-08: approach/face a target entity - [u16 0][from 3 floats][0x08][u32 0xC350 type][u32 targetHandle]
		// [0x01][9 zero bytes] (45-byte body / 49-byte frame). The NPC "turn to / move toward" a dynel. Validated 825/825.
		public static byte[] BuildApproachTargetBody(uint handle, Vector3 from, uint targetHandle)
		{
			return new FaolanStream()
				.WriteUInt32(GameServer0X2000Opcodes.MoveTaskIIR)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(handle)
				.WriteUInt16(0)
				.WriteVector3(from)
				.WriteByte(ModeApproachTarget)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(targetHandle)
				.WriteByte(0x01)
				.WriteUInt64(0)
				.WriteByte(0x00)
				.ToArray();
		}

		// mode-00: a bare position update - [u16 0][from 3 floats][0x00][0x01] (28-byte body / 32-byte frame). The
		// simplest move; the dominant shape for the player's own position ticks. Validated 74/74.
		public static byte[] BuildBarePositionBody(uint handle, Vector3 from)
		{
			return new FaolanStream().WriteUInt32(GameServer0X2000Opcodes.MoveTaskIIR).WriteUInt32(EntityClass.Character).WriteUInt32(handle).WriteUInt16(0).WriteVector3(from).WriteByte(ModeBarePosition).WriteByte(0x01).ToArray();
		}

		// Builds the 61-byte-frame MoveTask body for `handle` moving from `from` to `to`, facing `facing`. from==to is a
		// stationary orientation/position update (the capture's shape); from!=to is a step the client walks along.
		public static byte[] BuildBody(uint handle, Vector3 from, Vector3 to, Quaternion facing)
		{
			return new FaolanStream()
				.WriteUInt32(GameServer0X2000Opcodes.MoveTaskIIR)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(handle)
				.WriteUInt16(0)
				.WriteVector3(from)
				.WriteByte(0x02)
				.WriteVector3(to)
				.WriteQuaternion(facing)
				.WriteUInt16(0)
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
				.WriteUInt32(GameServer0X2000Opcodes.MoveTaskIIR)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(handle)
				.WriteUInt16(0)
				.WriteVector3(from)
				.WriteByte(ModePath)
				.WriteByte(0x01) // WaitMoveTask base header flags (bit0 -> no dur/rem floats)
				.WriteByte(0x00) // PathMoveTask F-flags (no optional floats; waypoint-list branch)
				.WriteUInt32(waypoints.Count);
			foreach (var w in waypoints)
				s.WriteVector3(w);
			return s
				.WriteByte(0x00) // trailer byte
				.WriteFloat(pathLength)
				.WriteUInt64(0)
				.WriteUInt32(0) // 12 zero bytes
				.WriteVector3(from)
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

		// One-way WALK from `from` to `to` (#272): a mode-1 PathMoveTask the client interpolates over time (a real walk),
		// unlike the mode-2 RotateMoveTask (`BuildBody`) which is a rotate/set-position the client SNAPS to. This matches
		// retail's 2-point movement-segment form exactly (capture-verified over 5763 records): header-flags 0x01 (bit0 SET
		// -> NO [duration][remaining]; retail does NOT pace a short 2-point segment by duration, it uses the entity's
		// movement-speed attribute), F-flags 0x00 (waypoint-list branch), a 2-point spline [from, to] (the client asserts
		// points->size() >= 2, splinepath.cpp:37 - a single point crashes it), and the 29-byte finalizer with
		// pathLength == the segment distance (capture ratio 1.000 for 2-point records) and its anchor at the DESTINATION
		// (anchoring at `from` made the NPC walk to the waypoint then BACK - the net-zero jitter that reverted the path
		// form before). NB the residual chase snap is NOT this packet form (it's retail-exact) but a SPEED mismatch: the
		// model's emit cadence (NpcMoveSpeed) must equal the speed the client renders the NPC at, else segments mis-chain.
		public static byte[] BuildWalkBody(uint handle, Vector3 from, Vector3 to)
		{
			var pathLength = Vector3.Distance(from, to);
			return new FaolanStream()
				.WriteUInt32(GameServer0X2000Opcodes.MoveTaskIIR)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(handle)
				.WriteUInt16(0)
				.WriteVector3(from)
				.WriteByte(ModePath)
				.WriteByte(0x01) // WaitMoveTask base header flags (bit0 SET -> no duration; retail 2-point form)
				.WriteByte(0x00) // PathMoveTask F-flags (no optional floats; waypoint-list branch)
				.WriteUInt32(2) // two control points: the spline needs >= 2 (client assert)
				.WriteVector3(from)
				.WriteVector3(to)
				.WriteByte(0x00) // trailer byte
				.WriteFloat(pathLength)
				.WriteUInt64(0)
				.WriteUInt32(0) // 12 zero bytes
				.WriteVector3(to) // anchor = DESTINATION (#272)
				.ToArray();
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
	}
}
