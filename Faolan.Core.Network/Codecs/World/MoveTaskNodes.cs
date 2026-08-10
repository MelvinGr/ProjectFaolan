using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.World
{
	// The SEMANTIC MoveTask forms, as VALUES. Not a codec (#530 rule 1): nothing here writes a byte - each factory
	// returns the MoveTaskNode that MoveTaskCodec.Build then encodes. They live beside the codec rather than as
	// Build overloads on it because several share a parameter list (a waypoint list plus a float is a path to one
	// form and a patrol to another), and choosing between those by signature is the ambiguity rule 1 removes.
	//
	// The "mode" byte selects the form (decoded from the captures, #53): 0x00 = a bare position, 0x01 = a
	// multi-waypoint path, 0x02 = the stationary from/to/facing form, 0x04 = a list of tasks, 0x05 = a forced move
	// (knockback, #496), 0x06 = an animated (stationary) task, 0x07 = formation-follow another entity (#741),
	// 0x08 = approach/face a target.
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

		// mode-08: approach/face a target entity - the NPC "turn to / move toward" a dynel. Validated 825/825.
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

		// mode-07: FORMATION-FOLLOW another entity - one NPC's movement target IS another NPC, held at a fixed offset
		// and re-sent as the leader's own path is re-sent (#741).
		//
		// This is what the maintainer's "giner & scruffy should be chasing each other" annotation names: in
		// NecroL80_..._annotated_1, `Ginger` 0x31C7A gets a 690-byte mode-04 path and 0x31CFD (`Scruffy`) gets a
		// 51-byte mode-07 naming 0x31C7A as its target - re-issued together, and Scruffy's own from-position is
		// within 1.3-3.1 units of Ginger's every time. So the ambient NPC-vs-NPC chase is NOT a scripted behaviour
		// and it is not mutual either: one NPC walks a path and the other is BOUND to it.
		// [V2: NecroL80_mage-necromancer_login_gameplay_annotated_1.tshark.json frames 1349 / 1521 / 2500 / 2836]
		//
		// ⭐ The six words are `[u32 class][u32 leaderHandle][f32 offsetX][f32 offsetY][f32 offsetZ][u32 slot]`, and
		// the FORMATION is what proves the split: the four `Black Dragon Guard Patroller`s that follow their
		// `Black Dragon Guard Captain Patroller` carry offsets on a 3x3 grid - x in {-1, 0, +1}, z in {0, -1, -3} -
		// each with its own slot INDEX in the last word, running 0..7. Scruffy's is `(0, 0, -4.5)` slot 0, i.e.
		// 4.5 units directly behind. offsetY is 0 in all 170 captured records.
		//
		// That is what retired the earlier reading of a single scalar "offset" in word 4: it happened to be right for
		// Scruffy and wrong for the 55 records whose displacement is on X instead.
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
		// ticks. Validated 74/74. HeaderFlags bit0 set -> no [duration][remaining] pair.
		public static MoveTaskNode BarePosition()
		{
			return new MoveTaskNode
			{
				Mode = ModeBarePosition,
				HeaderFlags = 0x01
			};
		}

		// mode-05: a FORCED MOVE - the server displacing an entity along a segment it did not ask for. This is how
		// knockback reaches the wire (#496); there is no dedicated knockback message.
		//
		// What makes this form identifiable at all is its RARITY. In the annotated dungeon capture there are 1,219
		// s2c MoveTaskIIRs; only 11 name the player as SUBJECT, nine of those are the trivial 31-32 byte anchor
		// records, and exactly ONE is this long form - at the instant the player's own annotation says `knockbacked`.
		// Its `to` is the position the player ends up at, which a SetPosIIR then pins 1.7 s later (and SetPosIIR
		// occurs twice in the whole capture: world entry, and here).
		//
		// ⚠️ ONE SAMPLE. `anchor` equals `from` in it, so whether the two can ever differ is unknown - the parameter
		// is kept separate rather than collapsed, because collapsing it would bake in an assumption the capture does
		// not test. The header's duration/remaining pair (1.0, 1.0) and the leading ballistic word (0.0) are likewise
		// single-observation and deliberately NOT named: duration/speed/gravity are all plausible and nothing
		// distinguishes them. Sent verbatim.
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

		// mode-01 PathMoveTask: the multi-waypoint path - decoded from the captures + the client reader FUN_00a483b0
		// (docs/re/movetask-grammar.md, #53). Canonical shape (92/92 captured else-branch records): the WaitMoveTask
		// base header with flags=0x01 (bit0 set -> no duration/remaining floats) + the PathMoveTask F-flags=0x00 (no
		// optional floats), then [u32 count][count x position] and a fixed 29-byte trailer
		//   [0x00][f32 pathLength][12x 00][anchor x/y/z].
		// `trailerScalar` is the path LENGTH for a one-way path and the patrol SPEED inside a patrol list - the same
		// slot, per form. It is a PARAMETER because the retail value differs from a recomputed float by ~1 ULP.
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

		// A patrol the client walks in a STATED TIME - retail's majority form, and the one that takes the pacing out of
		// the client's hands.
		//
		// With bit0 of the header SET (the `Patrol` overload below) there is no [duration][remaining] pair, and the
		// client paces the walk off the entity's own movement-speed attribute - which for an NPC is its RUN speed, so
		// the beat is sprinted (#714). Clearing bit0 and stating the seconds makes the walk take exactly that long
		// whatever the entity's attribute says.
		//
		// This is what retail does: of the 759 captured multi-point patrol path nodes, **535 carry the pair** (header
		// flags 0) and only 224 do not. `remaining` is the part of `seconds` still to run - equal to `seconds` for a
		// beat starting at its beginning, and less for one handed to a client that arrives mid-lap (313 of the 1,373
		// captured pairs have remaining < duration).
		public static MoveTaskNode PatrolTimed(IReadOnlyList<Vector3> waypoints, float speed, Vector3 anchor, float seconds, float remaining)
		{
			var list = Patrol(waypoints, speed, anchor);
			var path = list.Children[0];
			path.HeaderFlags = 0x00; // bit0 CLEAR -> the [duration][remaining] pair follows
			path.HeaderDuration = Bits(seconds);
			path.HeaderRemaining = Bits(remaining);
			return list;
		}

		// The canonical single-segment PATROL: a ListMoveTask holding the walked path plus a trailing wait.
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

		// The stored patrol tree (#316), rebuilt from its columnarized PatrolNode rows - any of the captured forms,
		// single- or multi-segment, re-emitted byte-exact.
		public static MoveTaskNode Patrol(ICollection<PatrolNode> nodes)
		{
			ArgumentNullException.ThrowIfNull(nodes);
			var root = nodes.FirstOrDefault(n => n.ParentPath == null) ?? throw new InvalidOperationException($"patrol for spawn {nodes.FirstOrDefault()?.NpcSpawnId} has no root node");
			return PatrolNodeMapper.ToTask(root);
		}

		// mode-02 (RotateMoveTask): move to `to` facing `facing`. from==to is a stationary orientation/position update
		// (the capture's shape); from!=to is a step the client SNAPS to.
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

		// One-way WALK from `from` to `to` (#272): a mode-1 PathMoveTask the client interpolates over time (a real walk),
		// unlike the mode-2 RotateMoveTask which is a rotate/set-position the client SNAPS to. This matches retail's
		// 2-point movement-segment form exactly (capture-verified over 5763 records): header-flags 0x01 (bit0 SET -> NO
		// [duration][remaining]; retail does NOT pace a short 2-point segment by duration, it uses the entity's
		// movement-speed attribute), F-flags 0x00 (waypoint-list branch), a 2-point spline [from, to] (the client asserts
		// points->size() >= 2, splinepath.cpp:37 - a single point crashes it), and the 29-byte finalizer with
		// pathLength == the segment distance (capture ratio 1.000 for 2-point records) and its anchor at the DESTINATION
		// (anchoring at `from` made the NPC walk to the waypoint then BACK - the net-zero jitter that reverted the path
		// form before). NB the residual chase snap is NOT this packet form (it's retail-exact) but a SPEED mismatch: the
		// model's emit cadence (NpcMoveSpeed) must equal the speed the client renders the NPC at, else segments mis-chain.
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
