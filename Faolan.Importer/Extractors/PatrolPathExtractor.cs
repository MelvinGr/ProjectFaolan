using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Faolan.Core.Constants;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;

namespace Faolan.Importer.Extractors
{
	// One captured NPC patrol route: its map + anchor position, the flattened waypoints (for the loop-shape filter),
	// the capture handle, and the columnarized task tree Root (#316).
	// Carries the wire task AST, not a columnarized tree: the importer converts it per spawn via PatrolNodeMapper so
	// there is exactly one wire->row conversion and no hand-maintained copy to fall behind a new column.
	// ClassId is set only for a route on a class-specific map (Map.ClassSpecific), where (map, position) no longer
	// identifies one spawn - two classes' NPCs can stand on the same spot. Null everywhere else.
	public sealed record PatrolPathEntry(uint MapId, Vector3 Position, IReadOnlyList<Vector3> Waypoints, uint Handle, MoveTaskNode Task)
	{
		public ConanClass? ClassId { get; init; }
	}

	// Extracts NPC patrol routes from a capture's MoveTask packets (MoveTaskIIR 0x96C46740): tracks the current map
	// (MapChange 5D85BFC7), parses every MoveTask via the fully-decoded recursive grammar (MoveTaskCodec,
	// round-trips all 6225 captured records), and columnarizes the task tree into a PatrolNode tree (#316) so the
	// runtime CONSTRUCTS the patrol from DB columns rather than replaying a captured blob. Keyed by handle (the
	// same-session mob identity); dedup by (map, position) joins a route to its NPC. Sibling of NpcSpawnExtractor.
	public static class PatrolPathExtractor
	{
		// One patrol route per NPC in a capture (#266 patrol). The real route is the mode-1 PathMoveTask waypoint list,
		// usually nested in a mode-4 ListMoveTask; the whole tree is parsed and columnarized. Keyed by (map, handle) so
		// the importer correlates a route to its spawn; keeps the RICHEST route per NPC (the most complete path seen
		// that session).
		//
		// ⚠️ The MAP is half the key, and leaving it out is a real defect, not tidiness (#571): a handle is scoped to
		// the PLAYFIELD, so the low pool starts again when the player changes zone and the same number names a
		// different NPC on the other side of the handoff. Measured over the corpus (`--report handle-recycling`), 46
		// handles carry a second template and 46 of 46 span more than one map - there is not one same-map reuse. Keyed
		// on the bare handle, one zone's route was handed to the next zone's NPC.
		public static Dictionary<(uint MapId, uint Handle), PatrolPathEntry> ExtractByHandle(IReadOnlyList<CapturedPacket> packets)
		{
			var byHandle = new Dictionary<(uint MapId, uint Handle), PatrolPathEntry>();
			uint currentMap = 0;

			foreach (var p in packets)
			{
				if (!p.IsIirCarrier)
					continue;

				if (p.SubOpcode == PlayfieldFCodec.Opcode)
				{
					if (p.TryParse(PlayfieldFCodec.Parse, out var change))
						currentMap = change.MapId;
					continue;
				}

				if (p.SubOpcode != MoveTaskCodec.Opcode || currentMap == 0)
					continue;

				// A form the grammar can't parse should not happen (6225/6225 round-trip), so this only drops a capture
				// truncated mid-record.
				if (!p.TryParse(MoveTaskCodec.Parse, out var task))
					continue;

				if (task.Handle is 0 or >= EntityHandles.CharacterInstanceBase)
					continue; // a player's MoveTask, not an NPC patrol

				// Reject a task that APPROACHES A CHARACTER (a mode-8 node, MoveTaskNodes.ApproachTarget): walking to a
				// player is a scripted follow - an escort or a companion - and never an idle patrol. The loop-shape test
				// below does NOT catch it, because a long meandering escort walks far enough to look loop-shaped: Casilda's
				// rescue walk from her cage to the city gate came in as a 25-waypoint "patrol" on her spawn, so the world
				// tick wandered her along it from the moment she spawned - before the player had freed her - which both
				// moved her off the cage and cancelled her captive struggle pose (a walking NPC plays locomotion).
				if (ApproachesACharacter(task.Task))
					continue;

				var waypoints = new List<Vector3>();
				CollectPathWaypoints(task.Task, waypoints);
				if (waypoints.Count < 2)
					continue; // no real path (a wait/rotate/approach task, or a single point)

				// Reject a ONE-WAY path: a captured MoveTask is often the mob CHASING the capturing player (a straight
				// line to where the player stood). An idle PATROL loops back toward its start, so its net displacement is
				// small vs the distance walked; a chase's displacement ~= its length. Keep only loop-shaped paths (#266).
				var from = new Vector3(Bits(task.FromX), Bits(task.FromY), Bits(task.FromZ));
				var length = Vector3.Distance(from, waypoints[0]);
				for (var i = 1; i < waypoints.Count; i++)
					length += Vector3.Distance(waypoints[i - 1], waypoints[i]);
				if (length < 1f || Vector3.Distance(from, waypoints[^1]) / length > 0.6f)
					continue; // one-way (chase/approach), not an idle patrol loop

				if (!byHandle.TryGetValue((currentMap, task.Handle), out var existing) || waypoints.Count > existing.Waypoints.Count)
					byHandle[(currentMap, task.Handle)] = new PatrolPathEntry(currentMap, from, waypoints, task.Handle, task.Task);
			}

			return byHandle;
		}

		// Dedup by (map, spawn point); keep the first seen - like NpcSpawnExtractor.Merge.
		public static List<PatrolPathEntry> Merge(IEnumerable<PatrolPathEntry> all)
		{
			var seen = new Dictionary<(uint, float, float, float), PatrolPathEntry>();
			foreach (var e in all)
				seen.TryAdd((e.MapId, e.Position.X, e.Position.Y, e.Position.Z), e);

			return seen.Values.ToList();
		}

		private static float Bits(uint raw)
		{
			return BitConverter.UInt32BitsToSingle(raw);
		}

		// Whether a move-task tree contains a mode-8 approach node aimed at a CHARACTER (EntityClass.Character) - i.e.
		// the NPC walks to a PLAYER. That makes the whole task a scripted follow (escort / companion), never an idle
		// patrol, whatever shape its path happens to have.
		private static bool ApproachesACharacter(MoveTaskNode node)
		{
			if (node.Mode == MoveTaskNodes.ModeApproachTarget && node.ApproachType == EntityClass.Character)
				return true;

			foreach (var child in node.Children)
			{
				if (ApproachesACharacter(child))
					return true;
			}

			return false;
		}

		// Gathers every PathMoveTask (mode 1) waypoint in a move-task tree (recursing into mode-4 ListMoveTask). The
		// node stores 3 raw big-endian float-bit u32 per waypoint (kept as bits for exact round-trip).
		private static void CollectPathWaypoints(MoveTaskNode node, List<Vector3> waypoints)
		{
			if (node.Mode == 1)
			{
				for (var i = 0; i + 2 < node.Waypoints.Count; i += 3)
					waypoints.Add(new Vector3(Bits(node.Waypoints[i]), Bits(node.Waypoints[i + 1]), Bits(node.Waypoints[i + 2])));
			}

			foreach (var child in node.Children)
				CollectPathWaypoints(child, waypoints);
		}

	}
}
