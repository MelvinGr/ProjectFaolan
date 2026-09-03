using System;
using System.Collections.Generic;
using System.Numerics;
using Faolan.Core.Database.Tables;

namespace Faolan.GameServer.World
{
	// Where an NPC LIVES and how it moves about on its own: the spawn point it returns to, the harvested route or
	// wander anchors it walks, the leash-return state, and the respawn countdown. A component of WorldEntity (Gitea
	// #928 item 10) - one of the three shapes that type carried in one bag; an entity that is not an NPC holds an
	// empty one. Reads nothing off the entity: the three methods that need to know where it STANDS take the position.
	public sealed class NpcRoute
	{
		// Default time a killed NPC stays dead before the tick respawns it. Capture-measured (`--report corpse-timing`).
		//
		// ⭐ **A respawn comes back on a NEW entity handle**, and that is the whole reason this was hard to see. A
		// handle is stable for as long as an entity LIVES - the same one re-spawns at identical coords across interest
		// churn - so the natural test, `death(H)` then `spawn(H)`, looks right and finds NOTHING. The revived creature
		// is a new entity with a new id. The signal is the same CREATURE reappearing AT the dead one's spawn point:
		//
		//   Brutal Pirate   149.9  149.9  149.9  150.0  150.2  150.2   (6 kills, 2 captures)  -> 150 s
		//   Wild Cow        119.5  120.0                               (2 kills, 2 captures)  -> 120 s
		//   Wild Pig        119.9  (192.9, a respawn the client was out of range for)          -> 120 s
		//
		// Six Brutal Pirate observations spanning 0.3 s across two unrelated captures is a timer, not a coincidence.
		// So the interval is **per creature**, in the 2-2.5 minute range, and this default takes the best-attested
		// cluster. `NpcTemplate` has no column for it yet - Gitea #855 - so every creature currently gets 150 s.
		//
		// ⚠️ Do NOT re-derive this from "the client watched for N seconds and nothing came back". That bound cannot
		// tell "no respawn" from "the client was out of range when it happened", and it is wrong by a factor of five
		// here: the same capture that shows Brutal Pirate 0xaa88 respawning at 150.0 s also has a 334 s window in
		// which it "never returned". Only observed intervals count.
		public const double DefaultRespawnDelaySeconds = 150.0;

		// Seconds until the patrol route is re-issued (retail re-paths a one-shot patrol so it keeps looping). Set to
		// the cycle length (path traversal time + dwell) when the route is issued; the tick counts it down.
		private double _patrolReissueRemaining;

		// #703: seconds a route walker stands still at the end of a beat before turning round; counts down instead of
		// stepping. Only the ENDS pause - a patrol that stopped at every waypoint would twitch its way down the street.
		private double _routeDwellRemaining;

		// Remaining time (seconds) until respawn while dead; meaningless when alive.
		private double _respawnRemaining;

		// #266 patrol: the captured waypoint route (absolute world coords) this NPC walks + its walk speed; empty for a
		// stationary NPC. Sent as a patrol MoveTask after the NPC spawns - retail drives idle patrol client-side (the
		// client walks the route), so the server just issues the captured route once per spawn/respawn.
		public IReadOnlyList<Vector3> PatrolWaypoints { get; set; } = [];

		// #703: a HARVESTED route is an ORDERED polyline - retail's own street-following path, sampled densely (the
		// median gap between consecutive waypoints is 5.8u across the 1,318 stored routes, p90 12.8u). It must be
		// walked waypoint BY waypoint, in order: the #266 wander instead picks a RANDOM anchor off the list and draws
		// one straight line to it, and in a city that line goes through whatever stands between - which is how Old
		// Tarantia's guards ended up walking through buildings. True only for a captured route; an ambient POOL's
		// points (#363) are scatter anchors within an area with no order to them, and keep the random drift.
		public bool WalksRoute { get; set; }

		// Whether the route closes on itself (last waypoint back at the first): a closed route is walked round and
		// round, an open one is walked back DOWN again on arrival. Jumping from the last waypoint to the first on an
		// open route would draw exactly the cross-country straight line this change exists to remove - the two ends of
		// an Old Tarantia guard's beat are 77u and a block of houses apart.
		public bool RouteLoops { get; set; }

		// The waypoint the route walk is currently heading for, and which way along the list it is going.
		public int RouteIndex { get; set; }

		public bool RouteBackwards { get; set; }

		// Set while a route walker is doing something else (chasing, walking home): its index no longer describes where
		// it stands, so the walk rejoins at the NEAREST waypoint rather than resuming a leg it has been dragged off.
		public bool RouteInterrupted { get; set; }

		public Quaternion SpawnRotation { get; set; }

		// Where the entity respawns (its original spawn point) - set for NPCs by ForNpc.
		public Vector3 SpawnPosition { get; set; }

		// A summoned/test NPC that should NOT revive when killed: once dead it lingers as a lootable
		// corpse for RespawnDelaySeconds, then the tick fades + removes it instead of respawning it.
		public bool NoRespawn { get; set; }

		// #266 retail wander: a beach NPC with captured anchor points drifts between them - capture-measured as a SINGLE
		// short 2-point MoveTask to a nearby anchor every ~25-60s (NOT a continuous route, NOT client-driven). The server
		// owns the position (every client sees the same spot; a chase begins from where it actually is, not its spawn).
		public bool Patrols => PatrolWaypoints.Count > 0;

		// True while a leashed NPC is walking back to its spawn point - it ignores re-aggro until home (so a player
		// camping the leash edge can't ping-pong it).
		public bool Returning { get; set; }

		// #804: when this NPC last found itself with NO target while away from home, or null if it has one. The tick
		// only turns it for home once WorldState.NpcChaseDeaggroDelaySeconds have passed since - retail's
		// `AIDefaults_ChaseDeaggroDelayTime`. Cleared on every tick that resolves a target, so it measures an
		// uninterrupted absence rather than a total.
		public DateTimeOffset? TargetLostAt { get; set; }

		// The captured MoveTask record re-addressed to THIS entity. For a route walker this IS what the client is sent
		// (#716) - the real multi-waypoint patrol form, issued once per cycle so the CLIENT walks the beat, rather than
		// the 2-point chase form re-sent every ~6u.
		public byte[] PatrolBody { get; set; } = null!;

		// #714: seconds still to wait before a freshly-streamed-in viewer is told the beat. Retail does NOT send the
		// patrol with the spawn - over 2,519 captured NPCs whose spawn AND first patrol were both seen, the gap runs
		// p10 1.54s / p50 4.02s / p90 32.05s, and only 3% arrive within 0.1s of the spawn. We sent it in the same
		// instant, which handed the client a TIMED walk for an entity it had not yet settled; it reconciled by dashing,
		// which is the catch-up run seen on a fresh spawn's first laps. Counts down in the tick; 0 = nothing pending.
		public double RoutePatrolDelayRemaining { get; set; }

		// #716: the client needs (re-)telling what walk it is on - at the start of a lap, and after anything took the
		// NPC off its beat (a chase, a leash home). Set true at spawn so the first lap is issued.
		public bool RouteReissueDue { get; set; } = true;

		// #714: the speed this NPC walks its beat at (u/s). Both the MODEL and the patrol body we hand the client use
		// it, so the server's idea of where the NPC is tracks what is rendered - the two drifting apart is what put a
		// killed patroller's corpse in the wrong place. Capture-measured; see WorldState.RouteWalkSpeed.
		public float RouteSpeed { get; set; } = WorldState.RouteWalkSpeed;

		public double PatrolCycleSeconds { get; set; }

		// How long this entity stays dead before the tick respawns it.
		public double RespawnDelaySeconds { get; set; } = DefaultRespawnDelaySeconds;

		// Arms the next wander move for a randomized ~25-60s (capture-measured retail cadence: a beach NPC drifts every
		// 30-80s, the spread keeps a pack from moving in lockstep).
		public void ArmPatrolCycle()
		{
			_patrolReissueRemaining = 25.0 + Random.Shared.NextDouble() * 35.0;
		}

		// #565: whether the entity has strayed beyond `leash` from its TERRITORY - what the leash check really wants to
		// know. `homeDistance` is the horizontal distance to its spawn, which is the whole story for a stationary NPC.
		// For a PATROLLER the route is territory too: a captured route can run hundreds of units, so an anchor past the
		// leash is a legitimate destination, not a player dragging it. Without this the #266 wander and the leash fight
		// each other - the wander assigns a far anchor, the next tick reads it as "led too far from spawn" and walks the
		// NPC straight back off its own route, every cycle. Only a position far from spawn AND from the route itself is a
		// real stray, so a dragged patroller still leashes.
		//
		// #716: "the route" is the POLYLINE, not its vertices. Measuring to the waypoints alone leaves the MIDDLE of a
		// long leg outside the NPC's own territory - and the walk goes straight down that middle. Over the 1,073 stored
		// beats that put 28 of them (all map 2070, 22 being the single-leg animal wanders whose one leg runs up to 228u)
		// permanently in leash-return: the walker sets off, escapes its territory ~40u out, turns round, streams the walk
		// home as ~6u chase segments, rejoins at the nearest waypoint and starts over - so it never once reached the far
		// end of its beat, and paid MoveTask traffic for the ping-pong.
		public bool BeyondLeash(Vector3 position, float homeDistance, float leash)
		{
			if (homeDistance <= leash)
				return false; // inside the leash of its spawn: nothing a route could add

			var here = new Vector3(position.X, 0f, position.Z); // horizontal, like homeDistance
			for (var i = 0; i < PatrolWaypoints.Count; i++)
			{
				var to = new Vector3(PatrolWaypoints[i].X, 0f, PatrolWaypoints[i].Z);
				var from = i == 0 ? to : new Vector3(PatrolWaypoints[i - 1].X, 0f, PatrolWaypoints[i - 1].Z);
				if (DistanceToSegment(from, to, here) <= leash)
					return false;
			}

			return true;
		}

		// #703: the waypoint the route walk is heading for. Only meaningful while WalksRoute (a route always has at
		// least one waypoint - Patrols is exactly PatrolWaypoints.Count > 0).
		public Vector3 RouteWaypoint => PatrolWaypoints[Math.Clamp(RouteIndex, 0, PatrolWaypoints.Count - 1)];

		// #703: arrived at the current waypoint - take the next one along the route. A closed route wraps; an open one
		// turns round at each end and walks back down the same polyline, so the NPC is never off the harvested path.
		// Returns true when it turned round or completed a lap, which is where the walk pauses (retail patrols dwell at
		// the ends of a beat rather than pivoting on the spot).
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
				// Round again from the other end - on a closed route that step IS a route segment, not a shortcut.
				RouteIndex = RouteBackwards ? PatrolWaypoints.Count - 1 : 0;
				return true;
			}

			RouteBackwards = !RouteBackwards;
			RouteIndex = Math.Clamp(RouteIndex + (RouteBackwards ? -1 : 1), 0, PatrolWaypoints.Count - 1);
			return true;
		}

		// #703: (re)join the route at the waypoint nearest to where the entity actually stands. Called when the route
		// walk starts and after anything that moved it off its leg (a chase, a leash walk home) - resuming the stale
		// index would send it back across the map to a waypoint it left behind. Horizontal distance: the route carries
		// real altitudes (an Old Tarantia beat climbs a staircase, 99 -> 112) and a waypoint one floor up is still the
		// nearest place to rejoin.
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

		// The next wander destination: an anchor point other than where we already are (so the move isn't a no-op).
		// `isOccupied` (#363 ambient pools): when supplied, PREFER an anchor no other entity is parked on, so pool
		// members spread across their area's points instead of stacking; falls back to any non-current anchor when
		// every free one is taken. Null (captured patrols) keeps the plain random-non-current pick.
		public Vector3 NextWanderPoint(Vector3 position, Func<Vector3, bool>? isOccupied = null)
		{
			if (PatrolWaypoints.Count == 1)
				return PatrolWaypoints[0];

			// Scan the anchors from a random offset (keeps the pick random) and take the first that isn't where we
			// already stand; among those, a FREE one wins, else remember the first taken anchor as the fallback.
			var start = Random.Shared.Next(PatrolWaypoints.Count);
			var fallback = position;
			var haveFallback = false;
			for (var i = 0; i < PatrolWaypoints.Count; i++)
			{
				var p = PatrolWaypoints[(start + i) % PatrolWaypoints.Count];
				if (Vector3.DistanceSquared(p, position) < 1f)
					continue; // a no-op move to where we already are

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

		// Counts down the wander timer; true when it's time for the next 2-point wander move (then re-arm via ArmPatrolCycle).
		public bool TickPatrolCycle(double dt)
		{
			if (!Patrols)
				return false;

			_patrolReissueRemaining -= dt;
			return _patrolReissueRemaining <= 0;
		}

		// #703: pauses a route walker for `seconds` (the dwell at the end of a beat).
		public void ArmRouteDwell(double seconds)
		{
			_routeDwellRemaining = seconds;
		}

		// #703: whether a route walker is currently PAUSED at the end of its beat rather than walking it. The one state in
		// which a patroller is genuinely standing still, which is what #766's ambient animations need to know.
		public bool RouteDwelling => _routeDwellRemaining > 0;

		// #703: counts the route dwell down; true once the walk may step again (so an un-armed walker walks straight on).
		public bool TickRouteDwell(double dt)
		{
			if (_routeDwellRemaining <= 0)
				return true;

			_routeDwellRemaining -= dt;
			return _routeDwellRemaining <= 0;
		}

		// Distance from `p` to the SEGMENT a->b (not to the infinite line, and not merely to its ends). A degenerate
		// segment (a == b - the first waypoint's self-segment) falls out as the point distance.
		private static float DistanceToSegment(Vector3 a, Vector3 b, Vector3 p)
		{
			var ab = b - a;
			var lengthSquared = ab.LengthSquared();
			if (lengthSquared < 1e-6f)
				return Vector3.Distance(a, p);

			var t = Math.Clamp(Vector3.Dot(p - a, ab) / lengthSquared, 0f, 1f);
			return Vector3.Distance(a + ab * t, p);
		}

		// The respawn countdown, armed on death and run down by the tick; the REVIVE itself is the entity's
		// (WorldEntity.TickRespawn) because it resets vitals, transform and loot - everything, not just the route.
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
