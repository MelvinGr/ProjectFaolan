using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Faolan.GameServer.World;

namespace Faolan.GameServer.Combat
{
	/// <summary>
	///     Picks the secondary targets a melee swing cleaves into.
	/// </summary>
	/// <remarks>
	///     <para>
	///         An AoC swing damages an area in front of the attacker, not just the selected target - Funcom's own
	///         quickstart: "Your attacks deal damage in a small area in front of you. You'll always do more damage to
	///         your primary target, but you should be mindful of other nearby enemies!"
	///     </para>
	///     <para>
	///         The cone is attacker-centred rather than target-centred, so it never cleaves into a mob standing behind
	///         the target and away from the attacker. Facing comes off the move record's yaw-only quaternion
	///         (docs/re/chardcmove-grammar.md - <c>2*atan2(qy, qw)</c>, within 7.5 degrees of the travel direction on
	///         90% of sustained straight runs).
	///     </para>
	///     <para>
	///         The directional arc a swing is aimed at (left/overhead/right) and its shield mitigation are a separate
	///         mechanic, and independent of this: the direction picks which of the defender's guards you meet, this
	///         cone picks who you meet.
	///     </para>
	/// </remarks>
	public static class CombatTargeting
	{
		// How far in front of the attacker a swing reaches. Provisional - no shipped number states it (the client's
		// tweak table carries no melee arc or reach entry; `TWK_Selection_Radius` 10 is targeting, not damage). Sized
		// as the NPC melee range (3u, WorldState.NpcMeleeRange) plus slack, so a swing at a target in contact still
		// catches the pack immediately around it.
		//
		// One number, deliberately: it bounds the swing itself - whether the selected target is reachable at all - as
		// well as the cleave splash, because they are the same physical reach. Two constants would let a swing land on
		// a primary the splash could not have touched, which is not a thing a sword does. It is also where a
		// weapon-dependent reach would go if one is ever recovered; nothing shipped states one.
		public const float SwingReach = 8f;

		// The cleave's reach is the swing's reach - see SwingReach. Its own name because the cleave call site reads
		// better for it, not because the two can differ.
		public const float DefaultCleaveReach = SwingReach;

		// The half-angle of the swing's cone, in degrees. Provisional for the same reason: 60 each side is a
		// 120-degree frontal arc - "a small area in front of you" that takes in the enemies flanking your target
		// without reaching the ones beside or behind you.
		public const float DefaultCleaveHalfAngleDegrees = 60f;

		// Default cap on secondary (cleave) targets per swing, so a swing into a big pack doesn't nuke the whole zone.
		public const int DefaultMaxCleaveTargets = 4;

		/// <summary>
		///     The hostile, living NPCs a swing by <paramref name="attacker" /> cleaves into: entities inside the
		///     frontal cone, ordered nearest-first and capped to <paramref name="max" />.
		/// </summary>
		/// <remarks>
		///     Players, dead entities and non-hostile NPCs are excluded - quest-givers and vendors standing nearby must
		///     never take splash. The primary target is excluded rather than cone-tested: it takes the swing by being
		///     selected, whatever angle it is at, and only the extras are earned geometrically.
		/// </remarks>
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

		/// <summary>
		///     Whether a swing by <paramref name="attacker" /> can physically reach the target the client named, and
		///     why not when it cannot.
		/// </summary>
		/// <remarks>
		///     <para>
		///         The target handle is the client's, so this is the only thing bounding a melee swing: the client
		///         checks a spell's range itself and never sends an out-of-range cast, but it always sends the swing.
		///     </para>
		///     <para>
		///         The target must be resolved through <see cref="WorldState.ZoneForEntity" /> - the attacker's
		///         (MapId, InstanceId) - and not through <c>TryGetZone(MapId)</c>, which resolves the default instance
		///         only and so misses on any <c>MapInstanceMode.Solo</c> map such as Tortage Island (4010), leaving the
		///         guard open. <c>MeleeReachTests</c> pins that case.
		///     </para>
		///     <para>
		///         A target the attacker's zone cannot place is <see cref="MeleeReach.TargetNotInZone" />, not
		///         reachable: you cannot hit what the world cannot put next to you, and letting it through only buys a
		///         hit-react and a floating damage number for a hit the damage path refuses anyway. The caller
		///         distinguishes the two because the causes differ - too far is the player's doing, not-in-zone is a
		///         targeting or interest bug on the server's side.
		///     </para>
		/// </remarks>
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

		/// <summary>
		///     An entity's facing as a yaw in radians, measured from +Z toward +X - the convention
		///     <c>Vector3.FaceTowards</c> builds and the one the move records are decoded in.
		/// </summary>
		/// <remarks>
		///     The quaternion carries the half-angle, hence the factor of two. Reading (qy, qw) as a heading pair
		///     instead scatters the result by tens of degrees.
		/// </remarks>
		public static double Facing(WorldEntity entity)
		{
			return 2.0 * Math.Atan2(entity.Rotation.Y, entity.Rotation.W);
		}

		/// <summary>
		///     Whether <paramref name="point" /> lies inside the cone of half-angle
		///     <paramref name="halfAngleDegrees" /> around <paramref name="facing" /> (radians) at
		///     <paramref name="from" />.
		/// </summary>
		/// <remarks>
		///     Height is ignored: the swing is a compass arc, and a target standing on a step is still in front of you.
		/// </remarks>
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

	/// <summary>
	///     Why a melee swing can or cannot land on the target the client named.
	/// </summary>
	/// <remarks>
	///     Only <see cref="InReach" /> lets the swing resolve. Both refusals must be answered as an air swing (the
	///     plain echo-ack) and never dropped: a silently refused action leaves the client waiting on a reply that
	///     never comes.
	/// </remarks>
	public enum MeleeReach
	{
		InReach,
		TooFar,
		TargetNotInZone
	}
}
