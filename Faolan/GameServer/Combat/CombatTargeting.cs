using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Faolan.GameServer.World;

namespace Faolan.GameServer.Combat
{
	// Picks the secondary targets a melee swing cleaves into (#18). An AoC swing damages an ARC, not just the
	// selected target. Without a decoded player-facing vector we model this as a TARGET-CENTERED cleave: the swing
	// that lands on the selected enemy also hits the other hostile, living NPCs clustered within `radius` of it -
	// geometrically robust (no rotation convention needed) and the right shape for a melee swing through a pack. The
	// directional arc + per-direction shield mitigation is the separate "Real Combat" decode (#20); this is the flat
	// "every nearby enemy takes the swing" half.
	public static class CombatTargeting
	{
		// Default cleave radius (world units) around the primary target. Provisional - tuned in-client; the aggro
		// range is 20, melee reach is much shorter, so a swing only catches a tight cluster.
		public const float DefaultCleaveRadius = 5f;

		// Default cap on secondary (cleave) targets per swing, so a swing into a big pack doesn't nuke the whole zone.
		public const int DefaultMaxCleaveTargets = 4;

		// The hostile, living NPCs a swing on `primary` cleaves into: other entities within `radius` of the primary,
		// excluding the primary itself, players, dead/un-killable (no vitals) entities, and non-hostile NPCs
		// (quest-givers/vendors standing nearby must never take splash). Ordered nearest-first and capped to `max`.
		public static IReadOnlyList<WorldEntity> CleaveTargets(Zone zone, WorldEntity primary, float radius, int max)
		{
			if (zone == null || primary == null || max <= 0)
				return Array.Empty<WorldEntity>();

			return zone
				.EntitiesInRange(primary.Position, radius, primary.Handle)
				.Where(e => !e.IsPlayer && e.Hostile && e.Vitals != null && !e.IsDead)
				.OrderBy(e => Vector3.Distance(e.Position, primary.Position))
				.Take(max)
				.ToList();
		}
	}
}
