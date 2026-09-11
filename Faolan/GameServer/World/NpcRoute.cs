using System;
using System.Collections.Generic;
using System.Numerics;
using Faolan.Core.Database.Tables;

namespace Faolan.GameServer.World
{
	/// <summary>
	///     Where an NPC lives and how it moves about on its own: the spawn point it returns to, the harvested route or
	///     wander anchors it walks, the leash-return state, and the respawn countdown.
	/// </summary>
	/// <remarks>
	///     A component of <see cref="WorldEntity" />; an entity that is not an NPC holds an empty one. It reads nothing
	///     off the entity - the methods that need to know where it stands take the position.
	/// </remarks>
	public sealed class NpcRoute
	{
		/// <summary>
		///     Default time a killed NPC stays dead before the tick respawns it, measured from captures
		///     (<c>--report corpse-timing</c>).
		/// </summary>
		/// <remarks>
		///     A respawn comes back on a new entity handle, so the interval is measured as the same creature
		///     reappearing at the dead one's spawn point, never as a spawn of the dead handle - a handle is stable only
		///     for as long as an entity lives:
		///     <code>
		///     Brutal Pirate   149.9  149.9  149.9  150.0  150.2  150.2   (6 kills, 2 captures)  -> 150 s
		///     Wild Cow        119.5  120.0                               (2 kills, 2 captures)  -> 120 s
		///     Wild Pig        119.9  (192.9, a respawn the client was out of range for)          -> 120 s
		///     </code>
		///     Six Brutal Pirate observations spanning 0.3 s across two unrelated captures is a timer rather than a
		///     coincidence, so the interval is per creature, in the two to two-and-a-half minute range, and this
		///     default takes the best-attested cluster. <c>NpcTemplate</c> carries no column for it, so every creature
		///     gets 150 s.
		///     <para>
		///         Do not re-derive this from "the client watched for N seconds and nothing came back": that bound
		///         cannot tell a missing respawn from one the client was out of range for, and it is wrong by a factor
		///         of five here - the same capture that shows Brutal Pirate 0xaa88 respawning at 150.0 s also has a
		///         334 s window in which it appears never to return. Only observed intervals count.
		///     </para>
		/// </remarks>
		public const double DefaultRespawnDelaySeconds = 150.0;

		// Seconds until the patrol route is re-issued (retail re-paths a one-shot patrol so it keeps looping). Set to
		// the cycle length (path traversal time + dwell) when the route is issued; the tick counts it down.
		private double _patrolReissueRemaining;

		// Seconds a route walker stands still at the end of a beat before turning round; it counts down instead of
		// stepping. Only the ends pause: a patrol that stopped at every waypoint would twitch its way down the street.
		private double _routeDwellRemaining;

		// Remaining time (seconds) until respawn while dead; meaningless when alive.
		private double _respawnRemaining;

		/// <summary>
		///     The captured waypoint route, in absolute world coordinates, this NPC walks; empty for a stationary NPC.
		/// </summary>
		/// <remarks>
		///     Sent as a patrol MoveTask after the NPC spawns. Retail drives idle patrol client-side - the client walks
		///     the route - so the server issues the captured route once per spawn or respawn.
		/// </remarks>
		public IReadOnlyList<Vector3> PatrolWaypoints { get; set; } = [];

		/// <summary>
		///     Whether the waypoints form a harvested, ordered polyline that must be walked waypoint by waypoint.
		/// </summary>
		/// <remarks>
		///     A harvested route is retail's own street-following path, sampled densely: the median gap between
		///     consecutive waypoints is 5.8 units across the 1,318 stored routes, p90 12.8. The wander instead picks a
		///     random anchor off the list and draws one straight line to it, which in a city goes through whatever
		///     stands between. True only for a captured route: an ambient pool's points are scatter anchors within an
		///     area, with no order to them, and keep the random drift.
		/// </remarks>
		public bool WalksRoute { get; set; }

		/// <summary>
		///     Whether the route closes on itself, its last waypoint back at the first. A closed route is walked round
		///     and round; an open one is walked back down again on arrival.
		/// </summary>
		/// <remarks>
		///     Jumping from the last waypoint to the first on an open route would draw a cross-country straight line:
		///     the two ends of an Old Tarantia guard's beat are 77 units and a block of houses apart.
		/// </remarks>
		public bool RouteLoops { get; set; }

		/// <summary>The waypoint the route walk is currently heading for.</summary>
		public int RouteIndex { get; set; }

		/// <summary>Which way along <see cref="PatrolWaypoints" /> the route walk is going.</summary>
		public bool RouteBackwards { get; set; }

		/// <summary>
		///     Set while a route walker is doing something else, such as chasing or walking home: its index no longer
		///     describes where it stands, so the walk rejoins at the nearest waypoint rather than resuming a leg it has
		///     been dragged off.
		/// </summary>
		public bool RouteInterrupted { get; set; }

		public Quaternion SpawnRotation { get; set; }

		/// <summary>Where the entity respawns: its original spawn point, set for NPCs by <c>WorldEntity.ForNpc</c>.</summary>
		public Vector3 SpawnPosition { get; set; }

		/// <summary>
		///     A summoned or test NPC that must not revive when killed: once dead it lingers as a lootable corpse for
		///     <see cref="RespawnDelaySeconds" />, then the tick fades and removes it instead of respawning it.
		/// </summary>
		public bool NoRespawn { get; set; }

		/// <summary>Whether this NPC drifts between anchor points rather than standing still.</summary>
		/// <remarks>
		///     Retail's wander is a single short two-point MoveTask to a nearby anchor every 25 to 60 seconds, not a
		///     continuous route and not client-driven. The server owns the position, so every client sees the same spot
		///     and a chase begins from where the NPC actually is rather than from its spawn.
		/// </remarks>
		public bool Patrols => PatrolWaypoints.Count > 0;

		/// <summary>
		///     True while a leashed NPC is walking back to its spawn point. It ignores re-aggro until home, so a player
		///     camping the leash edge cannot ping-pong it.
		/// </summary>
		public bool Returning { get; set; }

		/// <summary>
		///     When this NPC last found itself with no target while away from home, or null while it has one.
		/// </summary>
		/// <remarks>
		///     The tick only turns it for home once <c>WorldState.NpcChaseDeaggroDelaySeconds</c> have passed - retail's
		///     <c>AIDefaults_ChaseDeaggroDelayTime</c>. Cleared on every tick that resolves a target, so it measures an
		///     uninterrupted absence rather than a total.
		/// </remarks>
		public DateTimeOffset? TargetLostAt { get; set; }

		/// <summary>The captured MoveTask record re-addressed to this entity.</summary>
		/// <remarks>
		///     For a route walker this is what the client is sent: the multi-waypoint patrol form, issued once per cycle
		///     so the client walks the beat, rather than the two-point chase form re-sent every few units.
		/// </remarks>
		public byte[] PatrolBody { get; set; } = null!;

		/// <summary>Seconds still to wait before a freshly streamed-in viewer is told the beat.</summary>
		/// <remarks>
		///     Retail does not send the patrol with the spawn: over 2,519 captured NPCs whose spawn and first patrol
		///     were both seen, the gap runs p10 1.54 s, p50 4.02 s, p90 32.05 s, and only 3% arrive within 0.1 s of the
		///     spawn. Sending it in the same instant hands the client a timed walk for an entity it has not yet settled,
		///     and it reconciles by dashing. Counts down in the tick; 0 means nothing is pending.
		/// </remarks>
		public double RoutePatrolDelayRemaining { get; set; }

		/// <summary>
		///     Whether the client needs telling what walk this NPC is on - at the start of a lap, and after anything
		///     took it off its beat, such as a chase or a leash home. Set at spawn so the first lap is issued.
		/// </summary>
		public bool RouteReissueDue { get; set; } = true;

		/// <summary>The speed, in units per second, at which this NPC walks its beat.</summary>
		/// <remarks>
		///     Both the model and the patrol body handed to the client use it, so the server's idea of where the NPC is
		///     tracks what is rendered; the two drifting apart puts a killed patroller's corpse in the wrong place.
		///     Measured from captures - see <see cref="WorldState.RouteWalkSpeed" />.
		/// </remarks>
		public float RouteSpeed { get; set; } = WorldState.RouteWalkSpeed;

		public double PatrolCycleSeconds { get; set; }

		/// <summary>How long this entity stays dead before the tick respawns it.</summary>
		public double RespawnDelaySeconds { get; set; } = DefaultRespawnDelaySeconds;

		/// <summary>
		///     Arms the next wander move for a randomised 25 to 60 seconds, matching retail's measured cadence: a beach
		///     NPC drifts every 30 to 80 seconds, and the spread keeps a pack from moving in lockstep.
		/// </summary>
		public void ArmPatrolCycle()
		{
			_patrolReissueRemaining = 25.0 + Random.Shared.NextDouble() * 35.0;
		}

		/// <summary>Whether the entity has strayed beyond <paramref name="leash" /> from its territory.</summary>
		/// <remarks>
		///     For a stationary NPC the territory is its spawn point. For a patroller the route is territory too: a
		///     captured route can run hundreds of units, so an anchor past the leash is a legitimate destination rather
		///     than a player dragging it. Without that, the wander and the leash fight each other - the wander assigns
		///     a far anchor, the next tick reads it as led too far from spawn and walks the NPC straight back off its
		///     own route, every cycle. Only a position far from both the spawn and the route is a real stray, so a
		///     dragged patroller still leashes.
		///     <para>
		///         The territory is the polyline, not its vertices: measuring to the waypoints alone leaves the middle
		///         of a long leg outside the NPC's own territory, and the walk goes straight down that middle. Over the
		///         1,073 stored beats that would put 28 of them permanently in leash-return - all on map 2070, 22 of
		///         them single-leg animal wanders whose one leg runs up to 228 units - setting off, escaping about 40
		///         units out, turning round and rejoining at the nearest waypoint without ever reaching the far end.
		///     </para>
		/// </remarks>
		/// <param name="homeDistance">The horizontal distance from the entity to its spawn point.</param>
		public bool BeyondLeash(Vector3 position, float homeDistance, float leash)
		{
			if (homeDistance <= leash)
				return false; // inside the leash of its spawn, where a route can add nothing

			var here = new Vector3(position.X, 0f, position.Z); // horizontal, as homeDistance is
			for (var i = 0; i < PatrolWaypoints.Count; i++)
			{
				var to = new Vector3(PatrolWaypoints[i].X, 0f, PatrolWaypoints[i].Z);
				var from = i == 0 ? to : new Vector3(PatrolWaypoints[i - 1].X, 0f, PatrolWaypoints[i - 1].Z);
				if (DistanceToSegment(from, to, here) <= leash)
					return false;
			}

			return true;
		}

		/// <summary>
		///     The waypoint the route walk is heading for. Only meaningful while <see cref="WalksRoute" />, since a
		///     route always has at least one waypoint.
		/// </summary>
		public Vector3 RouteWaypoint => PatrolWaypoints[Math.Clamp(RouteIndex, 0, PatrolWaypoints.Count - 1)];

		/// <summary>
		///     Arrived at the current waypoint: take the next one along the route. A closed route wraps; an open one
		///     turns round at each end and walks back down the same polyline, so the NPC is never off the harvested
		///     path.
		/// </summary>
		/// <returns>
		///     Whether the walk turned round or completed a lap, which is where it pauses: retail patrols dwell at the
		///     ends of a beat rather than pivoting on the spot.
		/// </returns>
		public bool AdvanceRoute()
		{
			if (PatrolWaypoints.Count <= 1)
				return true;

			var next = RouteIndex + (RouteBackwards ? -1 : 1);
			if (next >= 0 && next < PatrolWaypoints.Count)
			{
				RouteIndex = next;
				return false;
			}

			if (RouteLoops)
			{
				// Round again from the other end: on a closed route that step is a route segment, not a shortcut.
				RouteIndex = RouteBackwards ? PatrolWaypoints.Count - 1 : 0;
				return true;
			}

			RouteBackwards = !RouteBackwards;
			RouteIndex = Math.Clamp(RouteIndex + (RouteBackwards ? -1 : 1), 0, PatrolWaypoints.Count - 1);
			return true;
		}

		/// <summary>
		///     Join or rejoin the route at the waypoint nearest to where the entity actually stands.
		/// </summary>
		/// <remarks>
		///     Called when the route walk starts and after anything that moved the NPC off its leg, such as a chase or
		///     a leash walk home: resuming the stale index would send it back across the map to a waypoint it left
		///     behind. The distance is horizontal, because the route carries real altitudes - an Old Tarantia beat
		///     climbs a staircase from 99 to 112 - and a waypoint one floor up is still the nearest place to rejoin.
		/// </remarks>
		public void RejoinRouteAtNearest(Vector3 position)
		{
			RouteInterrupted = false;
			if (PatrolWaypoints.Count == 0)
				return;

			var nearest = 0;
			var nearestDistance = float.MaxValue;
			for (var i = 0; i < PatrolWaypoints.Count; i++)
			{
				var dx = PatrolWaypoints[i].X - position.X;
				var dz = PatrolWaypoints[i].Z - position.Z;
				var distance = dx * dx + dz * dz;
				if (distance >= nearestDistance)
					continue;

				nearestDistance = distance;
				nearest = i;
			}

			RouteIndex = nearest;
		}

		/// <summary>
		///     The next wander destination: an anchor point other than the current position, so the move is not a
		///     no-op.
		/// </summary>
		/// <param name="isOccupied">
		///     When supplied, prefers an anchor no other entity is parked on, so ambient pool members spread across
		///     their area's points instead of stacking, falling back to any non-current anchor when every free one is
		///     taken. Null, for a captured patrol, keeps the plain random non-current pick.
		/// </param>
		public Vector3 NextWanderPoint(Vector3 position, Func<Vector3, bool>? isOccupied = null)
		{
			if (PatrolWaypoints.Count == 1)
				return PatrolWaypoints[0];

			// Scan the anchors from a random offset, which keeps the pick random, and take the first that is not the
			// current position; among those a free one wins, and the first taken anchor becomes the fallback.
			var start = Random.Shared.Next(PatrolWaypoints.Count);
			var fallback = position;
			var haveFallback = false;
			for (var i = 0; i < PatrolWaypoints.Count; i++)
			{
				var p = PatrolWaypoints[(start + i) % PatrolWaypoints.Count];
				if (Vector3.DistanceSquared(p, position) < 1f)
					continue; // a no-op move to the current position

				if (isOccupied == null || !isOccupied(p))
					return p;

				if (!haveFallback)
				{
					fallback = p;
					haveFallback = true;
				}
			}

			return fallback;
		}

		/// <summary>
		///     Counts the wander timer down.
		/// </summary>
		/// <returns>
		///     Whether it is time for the next two-point wander move, after which the caller re-arms with
		///     <see cref="ArmPatrolCycle" />.
		/// </returns>
		public bool TickPatrolCycle(double dt)
		{
			if (!Patrols)
				return false;

			_patrolReissueRemaining -= dt;
			return _patrolReissueRemaining <= 0;
		}

		/// <summary>Pauses a route walker for the dwell at the end of a beat.</summary>
		public void ArmRouteDwell(double seconds)
		{
			_routeDwellRemaining = seconds;
		}

		/// <summary>
		///     Whether a route walker is paused at the end of its beat rather than walking it - the one state in which a
		///     patroller is genuinely standing still, which is what the ambient animations need to know.
		/// </summary>
		public bool RouteDwelling => _routeDwellRemaining > 0;

		/// <summary>Counts the route dwell down.</summary>
		/// <returns>Whether the walk may step again, so an un-armed walker walks straight on.</returns>
		public bool TickRouteDwell(double dt)
		{
			if (_routeDwellRemaining <= 0)
				return true;

			_routeDwellRemaining -= dt;
			return _routeDwellRemaining <= 0;
		}

		/// <summary>
		///     Distance from <paramref name="p" /> to the segment <paramref name="a" />-<paramref name="b" />: not to
		///     the infinite line, and not merely to its ends. A degenerate segment, such as the first waypoint's
		///     self-segment, falls out as the point distance.
		/// </summary>
		private static float DistanceToSegment(Vector3 a, Vector3 b, Vector3 p)
		{
			var ab = b - a;
			var lengthSquared = ab.LengthSquared();
			if (lengthSquared < 1e-6f)
				return Vector3.Distance(a, p);

			var t = Math.Clamp(Vector3.Dot(p - a, ab) / lengthSquared, 0f, 1f);
			return Vector3.Distance(a + ab * t, p);
		}

		/// <summary>
		///     Arms the respawn countdown on death; the tick runs it down. The revive itself belongs to
		///     <c>WorldEntity.TickRespawn</c>, because it resets vitals, transform and loot, not just the route.
		/// </summary>
		public void ArmRespawn()
		{
			_respawnRemaining = RespawnDelaySeconds;
		}

		public bool TickRespawnDue(double dt)
		{
			_respawnRemaining -= dt;
			return _respawnRemaining <= 0;
		}
	}
}
