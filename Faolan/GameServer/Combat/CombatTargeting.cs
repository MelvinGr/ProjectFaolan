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
		//
		// ONE number, deliberately: it bounds the swing itself (#696 - whether the selected target is reachable at
		// all) as well as the cleave splash, because they are the same physical reach. Two constants would let a
		// swing legitimately land on a primary the splash could not have touched, which is not a thing a sword does.
		// ⚠️ It is also where a WEAPON-dependent reach would go if one is ever recovered - a polearm and a dagger
		// plainly do not share this, and nothing shipped tells us what either should be.
		public const float SwingReach = 8f;

		// The cleave's reach IS the swing's reach - see SwingReach. Kept as its own name because the cleave call site
		// reads better for it, not because the two can differ.
		public const float DefaultCleaveReach = SwingReach;

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
				.Where(e => e is { IsPlayer: false, Hostile: true, IsDead: false })
				.Where(e => IsInFrontOf(attacker.Position, facing, e.Position, halfAngleDegrees))
				.OrderBy(e => Vector3.Distance(e.Position, attacker.Position))
				.Take(max)
				.ToList();
		}

		// Whether a swing by `attacker` can physically REACH the target the client named (#696), and why not when it
		// cannot. The target handle is the CLIENT's, so this is the only thing bounding a melee swing - the client
		// checks a SPELL's range itself and never sends an out-of-range cast, but it always sends the swing.
		//
		// ⚠️ It resolves the target through `WorldState.ZoneForEntity`, i.e. the attacker's (MapId, InstanceId), and
		// that is the whole reason this is a pure function with a test rather than three lines inside CombatService.
		// The first cut of the check used `TryGetZone(MapId)`, which resolves the DEFAULT instance only; the map it
		// was tested on - Tortage Island (4010) - is `MapInstanceMode.Solo`, so the lookup missed, the guard fell
		// open, and the gate shipped doing nothing at all. `MeleeReachTests` now pins exactly that case.
		//
		// A target the attacker's zone cannot place is `TargetNotInZone`, NOT reachable: you cannot hit what the world
		// cannot put next to you, and letting it through only bought a hit-react and a floating damage number for a
		// hit `ApplyNpcDamage` then refuses anyway (#695). The caller distinguishes the two because the causes differ -
		// too far is the player's doing, not-in-zone is a targeting/interest bug on our side.
		public static MeleeReach ResolveSwingReach(WorldState world, WorldEntity attacker, uint targetHandle, out float distance)
		{
			distance = 0f;
			if (world == null || attacker == null || !world.ZoneForEntity(attacker).TryGet(targetHandle, out var target))
				return MeleeReach.TargetNotInZone;

			distance = Vector3.Distance(attacker.Position, target.Position);
			return distance <= SwingReach
				? MeleeReach.InReach
				: MeleeReach.TooFar;
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

	// Why a melee swing can or cannot land on the target the client named (#696). Only `InReach` lets the swing
	// resolve; both refusals are answered as an AIR SWING (the plain echo-ack), never dropped - a silently refused
	// action leaves the client waiting on a reply it will never get, which is the #271 lesson.
	public enum MeleeReach
	{
		InReach,
		TooFar,
		TargetNotInZone
	}
}
