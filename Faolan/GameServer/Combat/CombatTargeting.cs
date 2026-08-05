using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Faolan.GameServer.World;

namespace Faolan.GameServer.Combat
{
	// Picks the secondary targets a melee swing cleaves into (#18). An AoC swing damages an area IN FRONT OF THE
	// ATTACKER, not just the selected target - Funcom's own quickstart: "Your attacks deal damage in a small area in
	// front of you. You'll always do more damage to your primary target, but you should be mindful of other nearby
	// enemies!", and the wiki's "if you can get two or more enemies in front of you, your attacks can hit multiple
	// enemies".
	//
	// ⚠️ It used to be a TARGET-CENTERED sphere - every hostile within 5u of the selected enemy - on the grounds that
	// it needed no rotation convention. That is the wrong shape, and wrong in a way a player would feel: it cleaved
	// into a mob standing BEHIND the target and away from you, and missed the one beside you that the target happened
	// to be far from. The facing it was avoiding is decoded and has been for a while
	// (docs/re/chardcmove-grammar.md - a yaw-only quaternion, `2*atan2(qy, qw)`, within 7.5 degrees of the travel
	// direction on 90% of sustained straight runs), so there was nothing left to avoid.
	//
	// The directional arc a swing is AIMED at (left/overhead/right) and its shield mitigation are the separate "Real
	// Combat" decode (#20/#621), and the two are independent: the direction picks which of the DEFENDER's guards you
	// meet, this cone picks WHO you meet.
	public static class CombatTargeting
	{
		// How far in front of the attacker a swing reaches. PROVISIONAL - no shipped number states it (the client's
		// tweak table carries no melee arc/reach entry; `TWK_Selection_Radius` 10 is targeting, not damage). Sized as
		// the NPC melee range (3u, WorldState.NpcMeleeRange) plus the slack the old target-centered radius had, so a
		// swing at a target in contact still catches the pack immediately around it.
		public const float DefaultCleaveReach = 8f;

		// The half-angle of the swing's cone, in degrees. PROVISIONAL for the same reason: 60 each side is a
		// 120-degree frontal arc - "a small area in front of you" that takes in the enemies flanking your target
		// without reaching the ones beside or behind you.
		public const float DefaultCleaveHalfAngleDegrees = 60f;

		// Default cap on secondary (cleave) targets per swing, so a swing into a big pack doesn't nuke the whole zone.
		public const int DefaultMaxCleaveTargets = 4;

		// The hostile, living NPCs a swing by `attacker` cleaves into: entities inside the frontal cone, excluding the
		// primary target, players, dead/un-killable (no vitals) entities, and non-hostile NPCs (quest-givers and
		// vendors standing nearby must never take splash). Ordered nearest-first and capped to `max`.
		//
		// The PRIMARY is excluded rather than cone-tested: it takes the swing by being selected, whatever angle it is
		// at. Only the extras are earned geometrically.
		public static IReadOnlyList<WorldEntity> CleaveTargets(Zone zone, WorldEntity attacker, WorldEntity? primary, float reach,
			float halfAngleDegrees, int max)
		{
			if (zone == null || attacker == null || max <= 0)
				return [];

			var facing = Facing(attacker);
			return zone
				.EntitiesInRange(attacker.Position, reach, attacker.Handle)
				.Where(e => e.Handle != primary?.Handle)
				.Where(e => e is { IsPlayer: false, Hostile: true, Vitals: not null, IsDead: false })
				.Where(e => IsInFrontOf(attacker.Position, facing, e.Position, halfAngleDegrees))
				.OrderBy(e => Vector3.Distance(e.Position, attacker.Position))
				.Take(max)
				.ToList();
		}

		// An entity's facing as a yaw in radians, measured from +Z toward +X - the convention `Vector3.FaceTowards`
		// builds and the one the move records were decoded in. The quaternion carries the HALF-angle, hence the
		// factor of two; reading (qy, qw) as a heading pair is the mistake that made an earlier decode scatter by 57
		// degrees.
		public static double Facing(WorldEntity entity)
		{
			return 2.0 * Math.Atan2(entity.Rotation.Y, entity.Rotation.W);
		}

		// Whether `point` lies inside the cone of half-angle `halfAngleDegrees` around `facing` (radians) at `from`.
		// Height is ignored: the swing is a compass arc, and a target standing on a step is still in front of you.
		public static bool IsInFrontOf(Vector3 from, double facing, Vector3 point, float halfAngleDegrees)
		{
			var dx = point.X - from.X;
			var dz = point.Z - from.Z;
			if (dx == 0f && dz == 0f)
				return true; // standing inside the attacker: no bearing to read

			var bearing = Math.Atan2(dx, dz);
			var relative = Math.Atan2(Math.Sin(bearing - facing), Math.Cos(bearing - facing));
			return Math.Abs(double.RadiansToDegrees(relative)) <= halfAngleDegrees;
		}
	}
}
