using System.Collections.Generic;
using System.Numerics;
using Faolan.Core.Database.Tables;

namespace Faolan.GameServer.World
{
	// Picks where a dead player respawns (docs/server-architecture-roadmap.md): the CLOSEST resurrection point the
	// player KNOWS - either an always-known starter point or one they have discovered. Pure over the map's points +
	// the player's discovered set so it is unit-testable; the caller supplies the data and applies the move.
	public static class RespawnResolver
	{
		// The known respawn point closest to `from`, or null if the player knows none on this map.
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
