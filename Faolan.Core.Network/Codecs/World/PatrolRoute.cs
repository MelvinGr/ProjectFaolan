using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
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
			if (waypoints == null || waypoints.Count == 0)
				return 0f;

			var length = Vector3.Distance(from, waypoints[0]);
			for (var i = 1; i < waypoints.Count; i++)
				length += Vector3.Distance(waypoints[i - 1], waypoints[i]);
			return length;
		}

		private static void Collect(PatrolNode node, List<Vector3> waypoints)
		{
			if (node.Mode == 1)
			{
				foreach (var w in node.Waypoints.OrderBy(w => w.Ordinal))
					waypoints.Add(new Vector3(w.PositionX, w.PositionY, w.PositionZ));
			}

			foreach (var child in node.Children.OrderBy(c => c.Ordinal))
				Collect(child, waypoints);
		}
	}
}
