using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Faolan.Core.Database.Interfaces;
using Faolan.Core.Database.Tables;

namespace Faolan.GameServer.World
{
	// Picks where a dead player respawns (Gitea #404): the CLOSEST resurrection point the
	// player KNOWS - either an always-known starter point or one they have discovered. Pure over the map's points +
	// the player's discovered set so it is unit-testable; the caller supplies the data and applies the move.
	//
	// A criminal (#503) uses the OUTLAW half of the map's points instead (#517) - see Resolve.
	public static class RespawnResolver
	{
		// The point a player revives at, with the full fallback chain the callers all want: the closest KNOWN point
		// of the right kind, else any always-known point, else the map's first. Null only when the map has none.
		//
		// The criminal split (#517) is the client's own `shady` attribute on RDB 1000090: 49 outlaw points, one per
		// major playfield, all AlwaysKnown. A criminal prefers them and a non-criminal never gets one - a shady point
		// is closest to plenty of ordinary ground (they sit in the same playfields), so without the filter a
		// law-abiding player would sometimes release into an outlaw camp. Retail evidently just sends a criminal
		// there: every shady point is AlwaysKnown, so there is nothing to discover.
		//
		// The fallback matters: only 49 of 248 mapped playfields carry a shady point, so a criminal who dies anywhere
		// else takes the normal selection rather than nothing at all.
		public static RespawnPoint? Resolve(Vector3 from, IReadOnlyList<RespawnPoint> points, ISet<uint> discoveredIds, bool criminal)
		{
			if (points.Count == 0)
				return null;

			if (criminal && ClosestKnown(from, points.Where(p => p.Shady), discoveredIds) is { } outlaw)
				return outlaw;

			var normal = points.Where(p => !p.Shady).ToList();
			return ClosestKnown(from, normal, discoveredIds)
				?? normal.FirstOrDefault(p => p.AlwaysKnown)
				?? normal.FirstOrDefault()
				?? points[0];
		}

		// The known respawn point closest to `from`, or null if the player knows none in `points`.
		public static RespawnPoint? ClosestKnown(Vector3 from, IEnumerable<RespawnPoint> points, ISet<uint> discoveredIds)
		{
			RespawnPoint? best = null;
			var bestDistance = float.MaxValue;
			foreach (var point in points)
			{
				if (!IsKnown(point, discoveredIds))
					continue;

				var distance = Vector3.Distance(point.Position, from);
				if (distance < bestDistance)
				{
					bestDistance = distance;
					best = point;
				}
			}

			return best;
		}

		// True if `point` is usable as a respawn target for a player with `discoveredIds` (always-known, or discovered).
		public static bool IsKnown(RespawnPoint point, ISet<uint> discoveredIds)
		{
			return point.AlwaysKnown || (discoveredIds != null && discoveredIds.Contains(point.Id));
		}
	}
}
