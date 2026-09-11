using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Faolan.Core.Database.Interfaces;
using Faolan.Core.Database.Tables;

namespace Faolan.GameServer.World
{
	/// <summary>
	///     Picks where a dead player respawns: the closest resurrection point the player knows, either an always-known
	///     starter point or one they have discovered.
	/// </summary>
	/// <remarks>
	///     Pure over the map's points and the player's discovered set, so it is unit-testable: the caller supplies the
	///     data and applies the move. A criminal uses the outlaw half of the map's points instead - see
	///     <see cref="Resolve" />.
	/// </remarks>
	public static class RespawnResolver
	{
		/// <summary>
		///     The point a player revives at, with the full fallback chain: the closest known point of the right kind,
		///     else any always-known point, else the map's first. Null only when the map has none.
		/// </summary>
		/// <remarks>
		///     The criminal split is the client's own <c>shady</c> attribute on RDB 1000090: 49 outlaw points, one per
		///     major playfield, all always-known. A criminal prefers them and a non-criminal never gets one, because a
		///     shady point is closest to plenty of ordinary ground - they sit in the same playfields - so without the
		///     filter a law-abiding player would sometimes release into an outlaw camp. Retail simply sends a criminal
		///     there: every shady point is always-known, so there is nothing to discover. The fallback matters, because
		///     only 49 of 248 mapped playfields carry a shady point, and a criminal who dies anywhere else takes the
		///     normal selection rather than nothing at all.
		/// </remarks>
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

		/// <summary>
		///     The known respawn point closest to <paramref name="from" />, or null when the player knows none of them.
		/// </summary>
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

		/// <summary>
		///     Whether the point is usable as a respawn target for a player holding
		///     <paramref name="discoveredIds" /> - always-known, or discovered.
		/// </summary>
		public static bool IsKnown(RespawnPoint point, ISet<uint> discoveredIds)
		{
			return point.AlwaysKnown || (discoveredIds != null && discoveredIds.Contains(point.Id));
		}
	}
}
