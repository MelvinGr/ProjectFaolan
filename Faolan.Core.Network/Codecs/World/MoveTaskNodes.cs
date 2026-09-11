using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.World
{
	// The semantic MoveTask forms, as values. Not a codec: nothing here writes a byte - each factory returns the
	// MoveTaskNode that MoveTaskCodec.Build then encodes. They live beside the codec rather than as Build overloads on
	// it because several share a parameter list (a waypoint list plus a float is a path in one form and a patrol in
	// another), which a signature could not tell apart.
	//
	// The "mode" byte selects the form, as decoded from the captures: 0x00 = a bare position, 0x01 = a multi-waypoint
	// path, 0x02 = the stationary from/to/facing form, 0x04 = a list of tasks, 0x05 = a forced move (knockback),
	// 0x06 = an animated (stationary) task, 0x07 = formation-follow another entity, 0x08 = approach/face a target.
	public static class MoveTaskNodes
	{
		public const byte ModeApproachTarget = 0x08;
		public const byte ModeBarePosition = 0x00;
		public const byte ModeForcedMove = 0x05;
		public const byte ModeFormation = 0x07;
		public const byte ModeList = 0x04;
		public const byte ModePath = 0x01;
		public const byte ModeRotate = 0x02;
		public const byte ModeStationary = 0x06;
		public const byte ModeWait = 0x00;

		// The at-position-facing (mode 2) body length - the dominant form.
		public const int RotateBodyLength = 57;

		/// <summary>mode-08: approach/face a target entity - the NPC "turn to / move toward" a dynel. Validated 825/825.</summary>
		public static MoveTaskNode ApproachTarget(uint targetHandle)
		{
			var tail = new byte[10];
			tail[0] = 0x01;
			return new MoveTaskNode
			{
				Mode = ModeApproachTarget,
				ApproachType = EntityClass.Character,
				ApproachTarget = targetHandle,
				ApproachTail = tail
			};
		}

		// The companion-follow task retail sends for an escortee walking behind a player - a mode-04 list of two
		// children:
		//
		//   List(hdrFlags 1, listMin 0)
		//     [0] Path   - this segment's two waypoints, trailer scalar = the 3D segment distance, anchored at from
		//     [1] mode-8 - ApproachTarget bound to the leader's handle, tail [01][pi/4][pi/4][00]
		//
		// The second child is the point. A bare path is a line the client interpolates and then sits at the end of, so
		// the server has to keep re-anchoring it and the NPC snaps between segments. Binding the task to the leader
		// hands the follow to the client, which owns the position and the animation together, exactly as it does for a
		// patrol.
		//
		// Byte-exact against Casilda's follow in Hoxxling_mage-heraldofxotli_login_gameplay (her handle 0x00017ED0,
		// leader 0x01316E71) - see CasildaFollowMoveTask_MatchesCapture. The trailer scalar there is 7.0657, which is
		// the segment's own 3D length to six figures, so it is computed rather than carried as a constant.
		//
		// The path child anchors at `from` here where `Walk` anchors at `to`, and that difference is load-bearing:
		// `Walk` is a standalone segment whose anchor is its destination, while inside this list the mode-8 child owns
		// where the follower ends up and retail anchors the path at `from`.
		public static MoveTaskNode FollowLeader(Vector3 from, Vector3 to, uint leaderHandle)
		{
			var approach = ApproachTarget(leaderHandle);
			// Retail's tail is [01][pi/4][pi/4][00] where the bare factory leaves the nine trailing bytes zero.
			var quarterPi = BitConverter.GetBytes(Bits(MathF.PI / 4f));
			if (BitConverter.IsLittleEndian)
				Array.Reverse(quarterPi);
			approach.ApproachTail = [0x01, .. quarterPi, .. quarterPi, 0x00];

			return new MoveTaskNode
			{
				Mode = ModeList,
				HeaderFlags = 0x01, // bit0 SET -> no [duration][remaining], as retail sends
				ListMin = 0,
				Children = [Path([from, to], Vector3.Distance(from, to), from), approach]
			};
		}

		// mode-07: formation-follow another entity - one NPC's movement target is another NPC, held at a fixed offset
		// and re-sent as the leader's own path is re-sent. An ambient NPC-vs-NPC chase is this, not a scripted
		// behaviour, and it is not mutual: one NPC walks a path and the other is bound to it. In
		// NecroL80_..._annotated_1, `Ginger` 0x31C7A gets a 690-byte mode-04 path and 0x31CFD (`Scruffy`) gets a 51-byte
		// mode-07 naming 0x31C7A as its target, re-issued together, with Scruffy's own from-position within 1.3-3.1
		// units of Ginger's every time.
		// [V2: NecroL80_mage-necromancer_login_gameplay_annotated_1.tshark.json frames 1349 / 1521 / 2500 / 2836]
		//
		// The six words are `[u32 class][u32 leaderHandle][f32 offsetX][f32 offsetY][f32 offsetZ][u32 slot]`, and a
		// formation is what proves that split: the four `Black Dragon Guard Patroller`s that follow their
		// `Black Dragon Guard Captain Patroller` carry offsets on a 3x3 grid - x in {-1, 0, +1}, z in {0, -1, -3} - each
		// with its own slot index in the last word, running 0..7. Scruffy's is `(0, 0, -4.5)` slot 0, i.e. 4.5 units
		// directly behind. offsetY is 0 in all 170 captured records.
		public static MoveTaskNode Formation(uint targetHandle, Vector3 offset, uint slot = 0)
		{
			return new MoveTaskNode
			{
				Mode = ModeFormation,
				Formation6 =
				[
					EntityClass.Character, targetHandle, Bits(offset.X), Bits(offset.Y), Bits(offset.Z), slot
				]
			};
		}

		// mode-00: a bare position update - the simplest move, and the dominant shape for the player's own position
		// ticks. Validated 74/74. HeaderFlags bit0 set means no [duration][remaining] pair.
		public static MoveTaskNode BarePosition()
		{
			return new MoveTaskNode
			{
				Mode = ModeBarePosition,
				HeaderFlags = 0x01
			};
		}

		// mode-05: a forced move - the server displacing an entity along a segment it did not ask for. This is how
		// knockback reaches the wire; there is no dedicated knockback message.
		//
		// What makes this form identifiable is its rarity. In the annotated dungeon capture there are 1,219 s2c
		// MoveTaskIIRs; only 11 name the player as subject, nine of those are the trivial 31-32 byte anchor records,
		// and exactly one is this long form - at the instant the player's own annotation says `knockbacked`. Its `to`
		// is the position the player ends up at, which a SetPosIIR then pins 1.7 s later.
		//
		// That is one sample, and `anchor` equals `from` in it, so whether the two can ever differ is unknown: the
		// parameter is kept separate rather than collapsed, because collapsing it would bake in an assumption the
		// capture does not test. The header's duration/remaining pair (1.0, 1.0) and the leading ballistic word (0.0)
		// are likewise single-observation and deliberately unnamed - duration/speed/gravity are all plausible and
		// nothing distinguishes them - so they are sent verbatim.
		public static MoveTaskNode ForcedMove(Vector3 to, Vector3 anchor)
		{
			return new MoveTaskNode
			{
				Mode = ModeForcedMove,
				HeaderFlags = 0x00, // bit0 clear -> the [duration][remaining] pair follows
				HeaderDuration = Bits(1f),
				HeaderRemaining = Bits(1f),
				Ballistic7 = [Bits(0f), Bits(anchor.X), Bits(anchor.Y), Bits(anchor.Z), Bits(to.X), Bits(to.Y), Bits(to.Z)],
				Flag = 0x00
			};
		}

		// mode-01 PathMoveTask: the multi-waypoint path - decoded from the captures and the client reader FUN_00a483b0
		// (docs/re/movetask-grammar.md). Canonical shape (92/92 captured else-branch records): the WaitMoveTask base
		// header with flags=0x01 (bit0 set, so no duration/remaining floats) plus the PathMoveTask F-flags=0x00 (no
		// optional floats), then [u32 count][count x position] and a fixed 29-byte trailer
		//   [0x00][f32 pathLength][12x 00][anchor x/y/z].
		// `trailerScalar` is the path length for a one-way path and the patrol speed inside a patrol list - the same
		// slot, per form. It is a parameter because the retail value differs from a recomputed float by ~1 ULP.
		public static MoveTaskNode Path(IReadOnlyList<Vector3> waypoints, float trailerScalar, Vector3 anchor)
		{
			ArgumentNullException.ThrowIfNull(waypoints);
			var node = new MoveTaskNode
			{
				Mode = ModePath,
				HeaderFlags = 0x01, // bit0 SET -> no [duration][remaining]
				PathFlags = 0x00, // no optional floats; the waypoint-LIST branch
				TrailerByte = 0x00,
				TrailerWords = [Bits(trailerScalar), 0, 0, 0, Bits(anchor.X), Bits(anchor.Y), Bits(anchor.Z)],
				Waypoints = new List<uint>(waypoints.Count * 3)
			};
			foreach (var w in waypoints)
			{
				node.Waypoints.Add(Bits(w.X));
				node.Waypoints.Add(Bits(w.Y));
				node.Waypoints.Add(Bits(w.Z));
			}

			return node;
		}

		// A patrol the client walks in a stated time - retail's majority form, and the one that takes the pacing out of
		// the client's hands.
		//
		// With bit0 of the header set (the `Patrol` overload below) there is no [duration][remaining] pair, and the
		// client paces the walk off the entity's own movement-speed attribute - which for an NPC is its run speed, so
		// the beat is sprinted. Clearing bit0 and stating the seconds makes the walk take exactly that long whatever the
		// entity's attribute says.
		//
		// This is what retail does: of the 759 captured multi-point patrol path nodes, 535 carry the pair (header flags
		// 0) and only 224 do not. `remaining` is the part of `seconds` still to run - equal to `seconds` for a beat
		// starting at its beginning, and less for one handed to a client that arrives mid-lap (313 of the 1,373 captured
		// pairs have remaining < duration).
		public static MoveTaskNode PatrolTimed(IReadOnlyList<Vector3> waypoints, float speed, Vector3 anchor, float seconds, float remaining)
		{
			var list = Patrol(waypoints, speed, anchor);
			var path = list.Children[0];
			path.HeaderFlags = 0x00; // bit0 CLEAR -> the [duration][remaining] pair follows
			path.HeaderDuration = Bits(seconds);
			path.HeaderRemaining = Bits(remaining);
			return list;
		}

		// The canonical single-segment patrol: a ListMoveTask holding the walked path plus a trailing wait.
		// Reproduces the captured patrol body byte-for-byte (MoveTaskBuilderTests).
		public static MoveTaskNode Patrol(IReadOnlyList<Vector3> waypoints, float speed, Vector3 anchor)
		{
			if (waypoints == null || waypoints.Count == 0)
				throw new ArgumentException("a patrol needs at least one waypoint", nameof(waypoints));
			if (waypoints.Count > byte.MaxValue)
				throw new ArgumentOutOfRangeException(nameof(waypoints), "waypoint count exceeds one byte");

			var list = new MoveTaskNode
			{
				Mode = ModeList,
				HeaderFlags = 0x01,
				ListMin = 0x00
			};
			list.Children.Add(Path(waypoints, speed, anchor));
			list.Children.Add(new MoveTaskNode
			{
				Mode = ModeWait,
				HeaderFlags = 0x01
			});
			return list;
		}

		// The stored patrol tree, rebuilt from its columnarized PatrolNode rows - any of the captured forms, single- or
		// multi-segment, re-emitted byte-exact.
		public static MoveTaskNode Patrol(ICollection<PatrolNode> nodes)
		{
			ArgumentNullException.ThrowIfNull(nodes);
			var root = nodes.FirstOrDefault(n => n.ParentPath == null) ?? throw new InvalidOperationException($"patrol for spawn {nodes.FirstOrDefault()?.NpcSpawnId} has no root node");
			return PatrolNodeMapper.ToTask(root);
		}

		// mode-02 (RotateMoveTask): move to `to` facing `facing`. from == to is a stationary orientation/position update
		// (the capture's shape); from != to is a step the client snaps to.
		public static MoveTaskNode Rotate(Vector3 to, Quaternion facing)
		{
			return new MoveTaskNode
			{
				Mode = ModeRotate,
				Vec7 = [Bits(to.X), Bits(to.Y), Bits(to.Z), Bits(facing.X), Bits(facing.Y), Bits(facing.Z), Bits(facing.W)]
			};
		}

		// mode-06 AnimatedMoveTask: the minimal stationary record - the position in the record header and nothing else.
		public static MoveTaskNode Stationary()
		{
			return new MoveTaskNode
			{
				Mode = ModeStationary
			};
		}

		// One-way walk from `from` to `to`: a mode-1 PathMoveTask the client interpolates over time (a real walk),
		// unlike the mode-2 RotateMoveTask, which is a rotate/set-position the client snaps to. This matches retail's
		// 2-point movement-segment form exactly (capture-verified over 5763 records): header flags 0x01 (bit0 set, so no
		// [duration][remaining] - retail does not pace a short 2-point segment by duration, it uses the entity's
		// movement-speed attribute), F-flags 0x00 (waypoint-list branch), a 2-point spline [from, to] (the client
		// asserts points->size() >= 2, splinepath.cpp:37 - a single point crashes it), and the 29-byte finalizer with
		// pathLength == the segment distance (capture ratio 1.000 for 2-point records) and its anchor at the
		// destination. Anchoring at `from` instead makes the NPC walk to the waypoint and back.
		//
		// A chase that still snaps is not this packet form but a speed mismatch: the model's emit cadence (NpcMoveSpeed)
		// must equal the speed the client renders the NPC at, or segments mis-chain.
		public static MoveTaskNode Walk(Vector3 from, Vector3 to)
		{
			return Path([from, to], Vector3.Distance(from, to), to);
		}

		private static uint Bits(float value)
		{
			return BitConverter.SingleToUInt32Bits(value);
		}
	}
}
