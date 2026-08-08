using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Faolan.Core.Database.Interfaces;
using Faolan.Core.Database.Tables;

namespace Faolan.Core.Network.Codecs.World
{
	// Route geometry over a stored patrol tree - the world simulation's route + cycle-length source. Not a codec
	// (#530 rule 1): it reads the columnarized PatrolNode rows and computes distances; nothing encodes or decodes.
	public static class PatrolRoute
	{
		// The patrol's ordered waypoints (every PathMoveTask's waypoint list, walked in tree order).
		public static IReadOnlyList<Vector3> Waypoints(ICollection<PatrolNode> nodes)
		{
			ArgumentNullException.ThrowIfNull(nodes);

			var waypoints = new List<Vector3>();
			var root = nodes.FirstOrDefault(n => n.ParentPath == null);
			if (root != null)
				Collect(root, waypoints);

			return waypoints;
		}

		// Sum of the segment distances along from -> waypoints (the real walked length). The client uses it for
		// interpolation TIMING - a too-small value (e.g. the straight-line from->last) makes a winding patrol RUN to
		// cover the real path in the short time, so a multi-waypoint loop must report its full traversal length to
		// walk at the right pace. (For a straight path the sum equals from->last, matching the captured ratio-1.000
		// records; #266.)
		public static float TotalLength(Vector3 from, IReadOnlyList<Vector3> waypoints)
		{
			if (waypoints.Count == 0)
				return 0f;

			var length = Vector3.Distance(from, waypoints[0]);
			for (var i = 1; i < waypoints.Count; i++)
				length += Vector3.Distance(waypoints[i - 1], waypoints[i]);

			return length;
		}

		// The route as it is to be walked FROM WHERE THE NPC NOW STANDS: the waypoints still ahead of it, in the order
		// it will take them. `index` is the waypoint it is currently heading for, `backwards` which way along the list
		// it is going, `loops` whether the route's ends meet.
		//
		// Why this exists (#716): a patrol body is anchored at a position, and re-issuing one anchored at the entity's
		// SPAWN tells the client to restart the beat from there - which the player sees as the NPC teleporting back to
		// the start of its route the moment a lap ends. Anchoring at the live position and handing over only what is
		// left removes the jump; a CLOSED route continues round through the start rather than stopping at the list end,
		// so a loop stays a loop.
		public static IReadOnlyList<Vector3> FromHere(IReadOnlyList<Vector3> waypoints, int index, bool backwards, bool loops)
		{
			if (waypoints.Count == 0)
				return [];

			var ahead = new List<Vector3>(waypoints.Count);
			var step = backwards ? -1 : 1;
			for (var i = index; i >= 0 && i < waypoints.Count; i += step)
				ahead.Add(waypoints[i]);

			// A loop carries on through the far end and back to where it started, so the client walks a whole lap
			// rather than stopping short at the end of the list.
			if (loops)
			{
				var wrap = backwards ? waypoints.Count - 1 : 0;
				for (var i = wrap; i != index && i >= 0 && i < waypoints.Count; i += step)
					ahead.Add(waypoints[i]);
			}

			return ahead;
		}

		// #713: a stable id for the PLACEMENT a route belongs to - equal for two sightings of one patroller, different
		// for two genuinely different beats. Null when the spawn carries no route, which is the caller's cue that this
		// placement cannot be identified this way (a stationary NPC needs a different key entirely).
		//
		// Over the whole polyline rather than a summary: grouping the 857 stored routes by (length, segments, first
		// waypoint) and by the full polyline both give 626 groups, so the cheap key happens to be exact here - but it
		// is the polyline that MEANS "this beat", and a summary that collides would silently merge two placements.
		// Coordinates are rounded to a millimetre so a float that differs in its last bit cannot split one route in two.
		//
		// FNV-1a rather than string hashing: this value is PERSISTED, so it has to be identical across processes and
		// runs, which `string.GetHashCode` explicitly is not.
		public static uint? PlacementKey(ICollection<PatrolNode> nodes, uint templateId)
		{
			ArgumentNullException.ThrowIfNull(nodes);

			var waypoints = Waypoints(nodes);
			if (waypoints.Count == 0)
				return null;

			var hash = 2166136261u;
			Mix(ref hash, templateId);
			foreach (var w in waypoints)
			{
				Mix(ref hash, unchecked((uint)MathF.Round(w.X * 1000f)));
				Mix(ref hash, unchecked((uint)MathF.Round(w.Y * 1000f)));
				Mix(ref hash, unchecked((uint)MathF.Round(w.Z * 1000f)));
			}

			return hash;

			static void Mix(ref uint hash, uint value)
			{
				for (var i = 0; i < 4; i++)
					hash = unchecked((hash ^ (byte)(value >> (i * 8))) * 16777619u);
			}
		}

		private static void Collect(PatrolNode node, List<Vector3> waypoints)
		{
			if (node.Mode == 1)
				waypoints.AddRange(node.Waypoints.OrderBy(w => w.Ordinal).Select(w => w.Position));

			foreach (var child in node.Children.OrderBy(c => c.Ordinal))
				Collect(child, waypoints);
		}
	}
}
