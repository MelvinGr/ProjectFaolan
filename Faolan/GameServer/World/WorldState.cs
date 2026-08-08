using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using System.Threading;
using Faolan.Core.Database.Tables;

namespace Faolan.GameServer.World
{
	// The live world: one Zone per map, created on demand. The authoritative in-memory model the GameServer simulation
	// is built on (Gitea #404). A single instance is shared across the listener; entities are
	// placed when a player/NPC enters a map and removed on disconnect/despawn. Map changes move an entity between zones.
	public sealed class WorldState
	{
		// Seconds between an aggroed NPC's swings.
		public const double NpcAttackInterval = 2.0;

		// #262 companion-follow: how far behind the leading player a follower trails before it steps up (retail keeps the
		// follower ~one step behind; its MoveTasks track the player ~6-7u apart). A follower never leashes/aggros.
		public const float FollowStopRange = 4f;

		// #272 smooth movement: the client interpolates a MoveTask's from->to segment over the implied time, so the server
		// must feed it retail-sized segments - retail's chase/follow stream is ~6-7u steps. The model still steps Position
		// every tick (combat/interest fidelity), but a MoveTask is only broadcast once the entity has travelled this far
		// since the last one; a 1.2u-per-tick (10 Hz) MoveTask flood is what the client can't interpolate -> the jitter/
		// teleport (#272). At NpcMoveSpeed (7 u/s) this broadcasts ~once a second.
		public const float MoveBroadcastSegment = 6f;

		// ⚠️ #716: do NOT lengthen this to thin out idle-patrol traffic. Tried in-client 2026-08-08 (25u) and it trades a
		// packet-rate bug for a worse visual one, because a broadcast segment is the STRAIGHT LINE MoveFrom -> Position.
		// At ~6u the chord tracks the harvested polyline (median waypoint gap 5.8u), so the wire path IS the street; at
		// 25u the chord spans several waypoints and cuts the corner - which is exactly the through-the-buildings bug #703
		// existed to fix, now with the NPC covering the chord too fast and then standing still ("running / teleporting").
		//
		// The rate and the path cannot both be fixed by this constant: short segments follow the street at ~19 MoveTask/s,
		// long ones are cheap and cut corners. Retail's 0.38/s median says it does not server-stream idle patrol at all -
		// it issues the route and lets the CLIENT walk it. That is the real fix and it is #716's remaining work.

		// How close a player must be for a hostile NPC to aggro + attack (world units).
		public const float NpcAggroRange = 20f;

		// #714: the speed an NPC walks its idle beat at. CAPTURE-MEASURED, and measured the only way that answers the
		// question - by watching where retail's patrollers actually WERE over time, not by reading a field.
		//
		// Retail re-issues a patrol anchored at the NPC's then-current position, so consecutive re-issues of one handle
		// give ground speed directly (anchor delta / elapsed). Over 9,944 such pairs, bucketed by how far apart the two
		// re-issues were - a longer window contains more of the NPC's dwell, which drags the average below its walking
		// speed, so the CEILING of the short windows is the number wanted:
		//
		//   6-10s apart   n=1308   p50 1.34   p90 1.99   p99 2.69
		//   10-20s apart  n=3579   p50 1.09   p90 2.14   p99 2.23
		//
		// Both bands cap out at ~2.2 u/s however the window is cut, which is what a walking speed looks like when it is
		// being averaged with pauses. Hence 2.2.
		//
		// ⚠️ This deliberately does NOT come from the patrol's own trailer scalar, which `MoveTaskNodes.Path` describes
		// as "the patrol SPEED inside a patrol list". Read as a ground speed that field is simply wrong: it runs median
		// 6.75 u/s over 759 captured multi-point patrol nodes (p10 2.57, p90 14.37, max 159), 3-15x the speed the same
		// NPCs are OBSERVED moving at, and driving the model from it is what made Old Tarantia's Watchmen (scalar 31.73)
		// and Gingers (20.12) sprint their beats while the Scholar's Spirit (2.02) and Phocas (3.50) looked right. It is
		// a real per-route number that means something - it is just not u/s over the ground. Open on #714.
		public const float RouteWalkSpeed = 2.2f;

		// #703: how close a route walker must get to a waypoint to count as having reached it and take the next one.
		// Small relative to the 5.8u median gap between waypoints, so the walk stays ON the harvested polyline rather
		// than cutting each corner - the corners are what keep it in the street.
		public const float RouteWaypointReached = 0.75f;

		// #714: how long after a viewer streams an NPC in before its beat is issued. Retail's median gap between a
		// creature's spawn record and its first patrol (p50 4.02s over 2,519 captured NPCs; only 3% inside 0.1s).
		public const double RoutePatrolDelaySeconds = 4.0;

		// #703: how long a route walker stands at the end of its beat before turning round. Retail patrols pause at
		// the ends rather than pivoting on the spot; the number is ours (no capture pins it), same as PatrolDwellSeconds.
		public const double RouteTurnaroundDwellSeconds = 3.0;

		// #284 line-of-sight (tier 1, cheap heuristic): the max VERTICAL (Y/up) separation at which a hostile NPC will
		// still aggro a player. Aggro range is otherwise a 3D sphere, so without this an NPC aggros a player standing
		// directly above/below it - through a floor/ceiling or across a big drop (the most jarring "sees through
		// geometry" case). Gating on a vertical delta stops that with no collision data; true wall/terrain occlusion
		// (the RDB heightmap 1066611 / PhysX collision 1000012) is the higher tier tracked on #284. ~6u ≈ one storey.
		public const float NpcAggroVerticalRange = 6f;

		// #272: how close a chasing NPC actually WALKS UP to its target (well inside NpcMeleeRange so it reaches contact
		// and stays comfortably in swing range, not hovering exactly on the swing boundary where a drifting target leaves
		// it perpetually a step away and never swinging). Must be < NpcMeleeRange.
		public const float NpcContactRange = 1.5f;

		// How far (horizontal) an NPC will chase from its spawn before giving up and returning home (so a player can't
		// drag a mob across the whole zone). Once it disengages it keeps returning until home, ignoring re-aggro.
		public const float NpcLeashRange = 40f;

		// How long a mob stays engaged with whoever last hit it, with no further hits and nobody in aggro range. It is
		// the combat MEMORY: below this the mob is still fighting (so it chases, and does not reset its health), above
		// it the fight is over and the anti-whittle full-heal is allowed. Long enough to cover the gap between two
		// casts of a slow spell (Smite is a 2 s cast on a 1 s recast) plus the walk into range, so a ranged fight is
		// one continuous engagement rather than a series of first hits.
		public const double NpcCombatMemorySeconds = 10.0;

		// How close (horizontal) an aggroed NPC must be to its target to swing; beyond this it chases.
		public const float NpcMeleeRange = 3f;

		// How fast a chasing NPC closes on its target (world units per second). Capture-measured: retail renders NPC
		// locomotion at ~6.9 u/s (median over 207 clear-locomotion samples, mode 7; Captures movement trajectories). This
		// MUST equal the speed the CLIENT renders the NPC at, because the client paces a no-duration 2-point MoveTask
		// segment by the entity's movement speed, not by the packet: if the model steps faster than the client renders
		// (the old 12 u/s), the model outruns the on-screen NPC and each new segment makes it jump forward = the #272
		// chase teleport. 7 matches the render speed so consecutive segments chain smoothly. (Also ~PatrolWalkSpeed 6.)
		public const float NpcMoveSpeed = 7f;

		// A pet holds a tighter distance to its FORMATION SLOT than a lone escort keeps to the player, so the fan-out
		// slots stay distinct (a 4u stop would let two pets share one slot). #307.
		public const float PetFollowSlotStop = 1.5f;

		// The shared instance id every Shared map (and every pre-instancing call site) uses - one zone per map.
		public const uint DefaultInstance = 0;

		// Server-global monotonic allocator for playfield-instance tokens, mirroring retail's behaviour (a steadily
		// increasing per-instance id stamped into despawns; see protocol_despawn_n3toclientquit). Every Zone gets a
		// fresh token on creation as a FALLBACK. For a REPLAYED playfield the burst's own token then overrides it (the
		// client learned that one from the burst - PlayfieldTokenScan.TryExtract). This minted value becomes
		// the AUTHORITATIVE source once we GENERATE playfields (construct-don't-replay) and write the token into the
		// generated entry packets ourselves. Seeded clear of the observed retail pools (~0x004F.. / ~0x01EA..) so a
		// minted token is recognisably ours in a capture.
		// The single playfield-instance token every (burst-less) world-entry stamps - beach-derived, but threaded
		// through map-change / spawn / world-objects for EVERY playfield so the client keeps the abilities tab (a
		// token that disagrees across those packets makes the client hide it). Shared so the cross-map teleport
		// dest-announce can carry the same value the destination's entry will stamp (WorldBroadcaster).
		public const uint DefaultPlayfieldToken = 0x004FE428;
		private const double ChatterChancePerCheck = 0.05;

		// Ambient bystander chatter: on each check a friendly/neutral NPC has a small chance to bark, so barks stay
		// sparse + staggered across NPCs rather than all firing at once. Cosmetic; tune in the in-client pass.
		private const double ChatterCheckSeconds = 4.0;

		// How close a player must come for a DETECTION bark to fire (#694). Matched to `WorldSimulation.SayRangeUnits`
		// (50u), which is how far a bark carries: greeting someone who is too far to hear it would be worse than not
		// greeting them. PROVISIONAL - no shipped number states an NPC's detection radius, and the one annotated
		// capture times a crier line but does not pin the distance the player crossed to earn it.
		public const float GreetingRange = 50f;

		// The band a greeted player must leave before the NPC will greet them again - hysteresis, so a player loitering
		// on the greeting boundary is not greeted, forgotten and greeted again every few ticks. 1.5x the greeting
		// range; the exact factor is ours, only the fact that it must exceed GreetingRange matters.
		public const float GreetingForgetRange = GreetingRange * 1.5f;

		// The idle-heartbeat pulse (#225): a live NPC (re-)asserts its short-form BehaviorControlCenterIIR so the client
		// keeps playing its idle animation instead of freezing. The broadcast is gated to Confirmed viewers, so it never
		// fires at a still-loading client (the hang the earlier experiment hit).
		//
		// ⚠️ This interval is the PULSE, not the aggregate. It used to be applied to every live NPC on the same tick,
		// which made the wire rate scale with the zone's population: 1/s x ~120 visible NPCs in Old Tarantia measured at
		// 119.6 BCC/s to a single client, against a retail median of 0.93/s over the 91 corpus captures of >=60s (p90
		// 2.35, max 40.78 - and that max is an 89s resurrect burst, not a floor). ~130x the median (#716).
		//
		// So the budget below caps the AGGREGATE and the pulse rotates through the population by handle: every NPC still
		// heartbeats, just on a period that grows with how many there are. Interim - retail's real subject/cadence is
		// not pinned yet (0.93/s with dozens of NPCs in view is plainly not per-entity), and deriving it is #225's
		// remaining work. This only stops the flood.
		private const double HeartbeatIntervalSeconds = 1.0;

		// #716: the target aggregate BCC rate, in pulses per second per zone - retail's p90 (2.35/s), rounded down.
		private const int HeartbeatsPerSecond = 2;

		// The live zones keyed by (mapId, instanceId) (#268). A map can have several copies (instances): the default
		// shared instance plus any per-character Solo / per-party Group copies. Instance DefaultInstance (0) is the
		// shared copy every Shared map uses - so ZoneFor(mapId) / the historical single-zone-per-map paths all resolve
		// to it and behave exactly as before instancing existed.
		private readonly ConcurrentDictionary<(uint MapId, uint InstanceId), Zone> _zones = new ConcurrentDictionary<(uint MapId, uint InstanceId), Zone>();

		// Server-side terrain (RDB heightmap), for the tier-2 aggro line-of-sight below (#284). Null = terrain
		// unavailable (no client RDB); the line-of-sight check then fails open, leaving the tier-1 vertical gate.
		private readonly TerrainService? _terrain;
		private readonly TickAccumulator _chatter = new(ChatterCheckSeconds);
		private readonly TickAccumulator _heartbeat = new(HeartbeatIntervalSeconds);

		private uint _nextPlayfieldToken = 0x00A00000;

		// #716: which slice of the population this heartbeat pulse belongs to; advances once per pulse so the shards
		// take turns. Unsigned so the wrap is defined.
		private uint _heartbeatPhase;

		public WorldState(TerrainService? terrain = null)
		{
			_terrain = terrain;
		}

		// Mints the next playfield-instance token. Thread-safe (zones can be created from any connection).
		public uint NextPlayfieldToken()
		{
			return Interlocked.Increment(ref _nextPlayfieldToken);
		}

		// Adds the entity to the default shared instance of `mapId` (pre-instancing callers / tests). See the
		// instance-aware overload.
		public Zone Place(WorldEntity entity, uint mapId, Vector3 position, Quaternion rotation)
		{
			return Place(entity, mapId, DefaultInstance, position, rotation);
		}

		// Adds the entity to the (mapId, instanceId) zone with the given transform, removing it from its previous zone
		// if its map OR instance changed (#268). Returns the zone it now lives in so the caller can run interest
		// queries against it. The entity's MapId/InstanceId are updated to match - so every later ZoneForEntity lands
		// in this same copy.
		public Zone Place(WorldEntity entity, uint mapId, uint instanceId, Vector3 position, Quaternion rotation)
		{
			if ((entity.MapId != mapId || entity.InstanceId != instanceId) && _zones.TryGetValue((entity.MapId, entity.InstanceId), out var oldZone))
				oldZone.Remove(entity.Handle);

			entity.MapId = mapId;
			entity.InstanceId = instanceId;
			entity.Position = position;
			entity.Rotation = rotation;
			var zone = ZoneFor(mapId, instanceId);
			zone.Add(entity);
			return zone;
		}

		// Removes the entity from its current zone (disconnect / despawn).
		public void Remove(WorldEntity entity)
		{
			if (_zones.TryGetValue((entity.MapId, entity.InstanceId), out var zone))
				zone.Remove(entity.Handle);
		}

		// Picks (or creates) the instance a player entering `mapId` should join, given the map's instancing mode and
		// the player's EXISTING world entity (null on a fresh login) (#268). Same-map re-entry (respawn/teleport back)
		// keeps the player's current copy; a fresh entry mints a private copy for a Solo map and joins the shared copy
		// otherwise. Group is treated as Shared until party routing exists. The result is fed to Place.
		public uint ResolveEntryInstance(WorldEntity? existing, uint mapId, MapInstanceMode mode)
		{
			if (existing != null && existing.MapId == mapId && existing.InstanceId != DefaultInstance)
				return existing.InstanceId; // already in a private copy of this same map - stay in it

			return mode == MapInstanceMode.Solo
				? NextPlayfieldToken()
				: DefaultInstance;
		}

		// Advances the world model by `deltaSeconds` (Gitea #404) and returns the wire-visible
		// effects for the caller to broadcast: dead NPCs whose respawn delay elapsed (revived here - vitals + transform
		// reset), loot bags whose lifetime expired (despawned + removed), and aggroed NPCs swinging at a nearby player.
		// Player transforms are driven by Move packets, not the tick; aggroed NPCs chase here (NpcMove effects, broadcast
		// as MoveTask). The model stays network-free - it reports effects and the listener turns them into packets.
		public IReadOnlyList<WorldTickEffect> Tick(double deltaSeconds)
		{
			List<WorldTickEffect> effects = [];

			// #225: fire the idle heartbeat on its own cadence (once per HeartbeatIntervalSeconds), not every base tick.
			// Computed once per Tick, then applied inside the entity loop below.
			var heartbeatDue = _heartbeat.Advance(deltaSeconds, out _);
			var chatterDue = _chatter.Advance(deltaSeconds, out _);
			if (heartbeatDue)
				_heartbeatPhase++; // #716: next slice of the population takes this pulse

			foreach (var zone in _zones.Values) // Parralell?
			{
				// #716: how many pulses it takes to cover this zone once, so the AGGREGATE stays at HeartbeatsPerSecond
				// however many NPCs are standing in it. Sharding by handle (rather than by position in the iteration)
				// keeps an entity in the same slice from pulse to pulse - zone.Entities is a ConcurrentDictionary, whose
				// order is not stable, so a positional round-robin would skip and repeat entities at random.
				var heartbeatShards = heartbeatDue
					? (uint)Math.Max(1, zone.Entities.Count(e => e is { IsLoot: false, IsPlayer: false }) / Math.Max(1, HeartbeatsPerSecond))
					: 1u;
				var heartbeatShard = _heartbeatPhase % heartbeatShards;

				foreach (var entity in zone.Entities)
				{
					if (entity.IsLoot)
					{
						if (entity.TickExpiry(deltaSeconds))
							effects.Add(new WorldTickEffect(entity, WorldTickEffectType.LootExpired));
						continue;
					}

					if (entity.IsPlayer)
						continue;

					// A summon with a lifetime (the Frozen Hatred fatality pet, #140). Checked before the dead/respawn
					// logic so an expiring pet leaves cleanly rather than lingering as a corpse.
					if (entity.DespawnAt is { } despawnAt && DateTimeOffset.UtcNow >= despawnAt)
					{
						effects.Add(new WorldTickEffect(entity, WorldTickEffectType.TimedDespawn));
						continue;
					}

					// A dead NPC first lingers as the dying creature (death animation), then its corpse becomes a
					// lootable bag, then it respawns. The corpse-linger and respawn timers run independently off the
					// same death; the linger (~3s) elapses well before the respawn (~15s) so the bag is lootable in
					// between.
					if (entity.IsDead)
					{
						if (entity.TickCorpseLinger(deltaSeconds))
							effects.Add(new WorldTickEffect(entity, WorldTickEffectType.NpcCorpseLootable));
						// Summoned (test) NPCs don't revive: once the post-death timer elapses, fade + remove the
						// corpse (LootExpired) instead of respawning it.
						if (entity.NoRespawn)
						{
							if (entity.TickPostDeathDespawn(deltaSeconds))
							{
								effects.Add(new WorldTickEffect(entity, WorldTickEffectType.LootExpired));
								// #502: an encounter BOSS carries the lure it replaced. Once its corpse clears, put the
								// lure back where it stood so the encounter can fire again. Guarded so a corpse cannot
								// restore twice.
								if (entity is { EncounterLureTemplateId: not null, EncounterResetDone: false })
								{
									entity.EncounterResetDone = true;
									effects.Add(new WorldTickEffect(entity, WorldTickEffectType.EncounterReset));
								}
							}
						}
						else if (entity.TickRespawn(deltaSeconds))
							effects.Add(new WorldTickEffect(entity, WorldTickEffectType.NpcRespawned));

						continue;
					}

					// #225: past here the entity is a LIVE NPC. Pulse its idle heartbeat when due, independent of whether
					// it also moves this tick (retail streams the heartbeat continuously alongside any MoveTask).
					if (heartbeatDue && entity.Handle % heartbeatShards == heartbeatShard)
						effects.Add(new WorldTickEffect(entity, WorldTickEffectType.NpcHeartbeat));

					// #20: while engaged, the defender moves one shield to another arc every 5-12s (the captured cadence),
					// so the attacker has to keep re-aiming at the weakest arc. Per-entity timer, not a shared accumulator -
					// each captured NPC shifts on its own clock.
					if (entity.TickShieldShift(deltaSeconds, Random.Shared))
						effects.Add(new WorldTickEffect(entity, WorldTickEffectType.NpcShieldRose));

					// Ambient chatter: NPCs that have captured AMBIENT bark lines occasionally speak, staggered by the
					// per-NPC random roll so they don't all bark at once. The processor picks a random line + sends it
					// to nearby players (cosmetic). NPCs without a captured ambient group stay silent.
					if (chatterDue && entity.ChatterLines is { Length: > 0 } && Random.Shared.NextDouble() < ChatterChancePerCheck)
						effects.Add(new WorldTickEffect(entity, WorldTickEffectType.NpcChatter));

					// #694: a DETECTION bark is not on that clock. The crier greets a player he has NOTICED ARRIVE, so
					// the cue is the arrival itself - one line per player per approach, addressed at them by name.
					if (entity.GreetingLines is { Length: > 0 })
						DetectGreetings(zone, entity);

					// #262 companion-follow: a "released" NPC (e.g. Casilda after her rescue) follows its leading player -
					// step toward them and broadcast a MoveTask each step (UNGATED; not combat), the server-driven
					// incremental follow retail uses. Takes priority over patrol/aggro/leash. Clears when the leader
					// leaves the zone or logs out.
					if (entity.FollowTargetHandle is { } followTargetHandle)
					{
						if (!zone.TryGet(followTargetHandle, out var leader) || !leader.IsPlayer)
						{
							entity.FollowTargetHandle = null;
							EndMove(entity, true, WorldTickEffectType.NpcFollow);
							continue;
						}

						// A PET (OwnerHandle != 0) follows a per-pet FORMATION SLOT around the leader, not the leader's
						// exact spot, so multiple pets fan out instead of stacking (#307). The slot is deterministic
						// from the handle (stable, no per-tick jitter) and settles at a tighter stop so it holds its
						// place. Escort/quest followers (Casilda, OwnerHandle == 0) keep the exact-follow path (#262).
						var isPet = entity.OwnerHandle != null;
						var (ox, oz) = isPet
							? FollowFormationOffset(entity.Handle)
							: (0f, 0f);
						var stop = isPet
							? PetFollowSlotStop
							: FollowStopRange;
						var fdx = leader.Position.X + ox - entity.Position.X;
						var fdz = leader.Position.Z + oz - entity.Position.Z;
						var fdist = MathF.Sqrt(fdx * fdx + fdz * fdz);
						if (fdist > stop)
							StepAndStream(entity, fdx, fdz, fdist, stop, WorldTickEffectType.NpcFollow, leader);
						else
							// Caught up: flush the trailing approach (a real ~6u segment toward the leader, so its facing
							// points straight at her) and close the move with a single StopIIR (#262/#272).
							EndMove(entity, true, WorldTickEffectType.NpcFollow);
						continue;
					}

					if (entity.Vitals == null)
						continue;

					// #502 scripted encounter: a LURE is a harmless NPC that watches for an approaching player and, at
					// two measured distances, cries out and then swaps itself for a boss. Checked before the aggro block
					// because a lure is not hostile and would otherwise never look at players at all.
					if (entity.EncounterBossTemplateId != null)
					{
						var approaching = NearestPlayerInRange(zone, entity, MathF.Max(entity.EncounterBarkRadius, entity.EncounterTriggerRadius));
						if (approaching != null)
						{
							var edx = approaching.Position.X - entity.Position.X;
							var edz = approaching.Position.Z - entity.Position.Z;
							var edist = MathF.Sqrt(edx * edx + edz * edz);

							// The trigger wins when both are satisfied - a player who arrives inside the inner radius in
							// one step should spring the trap, not merely be shouted at.
							if (edist <= entity.EncounterTriggerRadius)
							{
								effects.Add(new WorldTickEffect(entity, WorldTickEffectType.EncounterTrigger, approaching));
								continue; // the lure is about to be replaced; skip the rest of its tick
							}

							if (!entity.EncounterBarked && edist <= entity.EncounterBarkRadius)
							{
								entity.EncounterBarked = true;
								effects.Add(new WorldTickEffect(entity, WorldTickEffectType.EncounterBark, approaching));
							}
						}
					}

					// Only hostile NPCs (mobs) aggro; talk-to NPCs (quest-givers/vendors) never attack.
					//
					// ⚠️ BEING HIT aggros too, not just proximity (#695). `NearestPlayerInRange` alone means a mob only
					// ever engages someone who walks inside NpcAggroRange, so a player attacking from further out - any
					// ranged attack, and any spell, which is most of them - hit a mob that never became engaged. That is
					// wrong on its own, and it was also load-bearing for something much worse: an unengaged mob takes
					// the `target == null` branch below and REVIVES TO FULL, every tick. So every hit after the first
					// was erased before the next one landed ("the first hit is fine, everything following is not"), and
					// the mob stood there while it happened.
					var target = entity.Hostile
						? NearestPlayerInRange(zone, entity, NpcAggroRange) ?? RecentAttackerOf(zone, entity)
						: null;

					var homeDx = entity.SpawnPosition.X - entity.Position.X;
					var homeDz = entity.SpawnPosition.Z - entity.Position.Z;
					var homeDist = MathF.Sqrt(homeDx * homeDx + homeDz * homeDz);

					// Leash: once led too far from its territory, disengage and keep returning home until arrived (ignoring
					// re-aggro) so a player hovering at the edge can't ping-pong it. A returning NPC walks back, then
					// heals to full on arrival. Territory is spawn plus - for a patroller - its route (#565: measuring
					// against spawn alone made the #266 wander and this check fight over every far anchor).
					if (entity.BeyondLeash(homeDist, NpcLeashRange))
						entity.Returning = true;

					// De-aggro return: when an NPC loses its target (the player left aggro range) while away from its
					// spawn, walk it back home instead of leaving it standing where the chase ended. Patrollers are
					// exempt - their route drives them; everyone else returns to origin (then heals on arrival, below).
					if (target == null && homeDist > NpcMeleeRange && !entity.Patrols)
						entity.Returning = true;

					if (entity.Returning)
					{
						SetCombat(entity, false); // disengaged: sheathe the weapon while walking home
						entity.ArmAttackCooldown(NpcAttackInterval);
						entity.RouteInterrupted = true; // #703: walked off its beat - rejoin at the nearest waypoint after
						if (homeDist > NpcMeleeRange)
						{
							// #565: the walk home also descends to the SPAWN's altitude. Movement otherwise keeps the
							// entity's current Y ("keep terrain Y"), which is right for a chase (the ground under the
							// path) but wrong here - the stray Y is whatever height it was led/wandered to, so the NPC
							// would walk the whole way home at that altitude and arrive floating above its spawn.
							StepAndStream(entity, homeDx, homeDz, homeDist, 0f, WorldTickEffectType.NpcMove, null!, entity.SpawnPosition.Y);
							continue;
						}

						entity.Returning = false;
						// #565: home again - plant the spawn's altitude. The walk down closes the height gap in proportion
						// to the horizontal one, but it stops NpcMeleeRange short of the spawn, so that same fraction of
						// the gap would be left standing. Arriving is the point to be exact about it.
						entity.Position = new Vector3(entity.Position.X, entity.SpawnPosition.Y, entity.Position.Z);
						entity.Vitals.ReviveToFull(); // home again
						EndMove(entity, true, WorldTickEffectType.NpcMove); // arrived: flush + stop (#190/#272)
						continue;
					}

					if (target == null)
					{
						SetCombat(entity, false); // no target in range: disengage and sheathe
						entity.ArmAttackCooldown(NpcAttackInterval); // re-aggro shouldn't swing instantly

						// #703: a ROUTE walker resumes its beat here, and must NOT be stopped first - EndMove would close
						// its move stream every idle tick. It keeps one continuous walk instead, so the flush/stop below is
						// for the wander and the chase, which genuinely end.
						if (entity.WalksRoute)
						{
							if (entity.Vitals.Health < entity.Vitals.MaxHealth && !entity.InCombatWithin(NpcCombatMemorySeconds))
								entity.Vitals.ReviveToFull();

							WalkRoute(entity);
							continue;
						}

						EndMove(entity, true, WorldTickEffectType.NpcMove); // stop if we were mid-chase (#272)
						// Disengaged with no one in range: heal back to full so it can't be whittled from out of range.
						//
						// ⚠️ Only once the fight is genuinely OVER. This used to fire on any tick with no player in
						// aggro range, which - because damage did not aggro (see the target line above) - meant a mob
						// shot from range healed to full between every single hit. The anti-whittle intent is right;
						// the trigger was "nobody stands next to me", which is not the same as "nobody is fighting me".
						// NpcCombatMemorySeconds after the last hit is, and it is also what keeps the mob chasing.
						if (entity.Vitals.Health < entity.Vitals.MaxHealth && !entity.InCombatWithin(NpcCombatMemorySeconds))
							entity.Vitals.ReviveToFull();
						// #266 retail wander (capture-verified): a beach NPC drifts with a SINGLE short 2-point move to a
						// nearby anchor every ~25-60s - NOT a continuous client-walked route. Server-driven: we move the
						// model to the destination and broadcast the one MoveFrom->Position segment, so every player sees the
						// same spot and a later chase starts from where the NPC actually is (no aggro-from-spawn teleport).
						if (entity.Patrols && entity.TickPatrolCycle(deltaSeconds))
						{
							entity.MoveFrom = entity.Position;
							// #363 ambient anti-stacking: an area-managed pool member (SpawnAreaId set) avoids drifting onto a
							// point another live entity already occupies (within ~2u, matching FreeAmbientPoint), so a pool
							// spreads across its area instead of piling up. Captured patrols (no SpawnAreaId) keep their route.
							Func<Vector3, bool>? occupied = entity.SpawnAreaId == null
								? null
								: pt =>
								{
									foreach (var other in zone.Entities)
									{
										if (ReferenceEquals(other, entity) || other.IsDead || other.IsLoot)
											continue;

										var odx = other.Position.X - pt.X;
										var odz = other.Position.Z - pt.Z;
										if (odx * odx + odz * odz < 4f)
											return true;
									}

									return false;
								};
							entity.Position = entity.NextWanderPoint(occupied);
							entity.ArmPatrolCycle();
							effects.Add(new WorldTickEffect(entity, WorldTickEffectType.NpcMove));
						}

						continue;
					}

					// Engaged: a target is in aggro range and we're not leashing home. Draw the weapon on the transition
					// (the captured combat-mode stat) BEFORE the chase/swing, mirroring retail - the stat-20 update lands
					// the tick the mob acquires its target, while it's still closing the distance (#218). The target rides
					// the effect so the broadcaster can also push the aggro (HateList/ThreatLevel, #216).
					SetCombat(entity, true, target);
					entity.RouteInterrupted = true; // #703: a chase leaves the beat; the walk rejoins at the nearest waypoint

					// Chase: if the target is beyond melee range, step toward it (horizontal plane; keep terrain Y). The move
					// is broadcast in retail-sized segments (StepAndStream throttle, #272) so the client interpolates the
					// run up to the player smoothly; it swings once it arrives in melee range.
					var dx = target.Position.X - entity.Position.X;
					var dz = target.Position.Z - entity.Position.Z;
					var horizontal = MathF.Sqrt(dx * dx + dz * dz);

					// Walk up to CONTACT range (well inside swing range) so the NPC visibly closes on the player instead of
					// stopping a swing-range away. While still outside swing range it only winds up; once inside swing range
					// it swings even while taking the last steps to contact (#272).
					if (horizontal > NpcContactRange)
						StepAndStream(entity, dx, dz, horizontal, NpcContactRange, WorldTickEffectType.NpcMove, target);
					else
						// At contact: flush the trailing approach step so the client sees it close the last bit (no StopIIR -
						// the swing/combat-mode draw plants it).
						EndMove(entity, false, WorldTickEffectType.NpcMove);

					if (horizontal > NpcMeleeRange)
					{
						entity.ArmAttackCooldown(NpcAttackInterval); // still out of swing range: wind up for arrival
						continue;
					}

					if (entity.TickAttackReady(deltaSeconds, NpcAttackInterval))
						// A caster NPC casts a spell on the same cadence; everyone else swings (#232).
					{
						effects.Add(new WorldTickEffect(entity, entity.IsCaster
							? WorldTickEffectType.NpcCast
							: WorldTickEffectType.NpcAttack, target));
					}
				}
			}

			return effects;

			// Flips an NPC's combat-engaged state and, on the transition, queues the combat-mode effect the broadcaster
			// turns into the stat-20 weapon draw/sheathe (#218). No-op when already in that state, so it fires once per
			// engage/disengage.
			void SetCombat(WorldEntity e, bool inCombat, WorldEntity? target = null)
			{
				if (e.InCombat == inCombat)
					return;

				e.InCombat = inCombat;
				effects.Add(new WorldTickEffect(e, inCombat
					? WorldTickEffectType.NpcEnterCombat
					: WorldTickEffectType.NpcExitCombat, target));

				// #20: engaging opens the defender's shield rose (and arms its first shift); disengaging closes it, so a
				// fresh fight starts on a fresh distribution the way each captured engagement does.
				if (inCombat)
				{
					e.OpenShieldRose(Random.Shared);
					effects.Add(new WorldTickEffect(e, WorldTickEffectType.NpcShieldRose, target));
				}
				else
				{
					e.ShieldDistribution = 0;
				}
			}

			// Queues a DETECTION bark (#694) for every player who has just walked into `e`'s greeting range, and forgets
			// the ones who have walked back out of it. This is the whole difference between a greeting and ambient
			// chatter: the cue is a player ARRIVING, so the line lands as you walk up instead of being said to an empty
			// street on a clock that runs whether anyone is there or not.
			//
			// The forget range is deliberately WIDER than the greeting range: with one radius a player standing right on
			// the boundary would be greeted, forgotten and greeted again every few ticks. The gap is hysteresis, not a
			// second concept.
			void DetectGreetings(Zone z, WorldEntity e)
			{
				foreach (var player in z.PlayersInRange(e.Position, GreetingForgetRange, e.Handle))
				{
					if (Vector3.Distance(player.Position, e.Position) > GreetingRange)
						continue; // inside the forget band but not yet close enough to greet - leave the state alone

					if (e.GreetedPlayers.Add(player.Handle))
						effects.Add(new WorldTickEffect(e, WorldTickEffectType.NpcGreeting, player));
				}

				if (e.GreetedPlayers.Count == 0)
					return;

				// Forget anyone who has left the band (or the zone entirely), so their next approach greets again.
				e.GreetedPlayers.RemoveWhere(handle => !z.TryGet(handle, out var p)
				                                       || Vector3.Distance(p.Position, e.Position) > GreetingForgetRange);
			}

			// Steps `e` toward (dx,dz) this tick and queues a move broadcast ONLY once it has travelled a retail-sized
			// segment since the last one (#272), so the client interpolates ~6u segments instead of a per-tick snap. The
			// model position advances every call regardless; `moveType` is NpcMove (chase/return) or NpcFollow (companion)
			// so the loop gates/handles it correctly. `target` rides the effect (the aggro/leader the broadcaster wants).
			void StepAndStream(WorldEntity e, float dx, float dz, float dist, float stopShort, WorldTickEffectType moveType, WorldEntity target, float? targetY = null)
			{
				if (!e.Moving) // a fresh move: anchor the first segment at the start point
				{
					e.MoveOrigin = e.Position;
					e.Moving = true;
				}

				StepToward(e, dx, dz, dist, deltaSeconds, stopShort, targetY);
				if (Vector3.DistanceSquared(e.MoveOrigin, e.Position) < MoveBroadcastSegment * MoveBroadcastSegment)
					return; // segment too short to broadcast yet - keep accumulating

				e.MoveFrom = e.MoveOrigin; // the segment the broadcaster sends (MoveFrom -> Position)
				e.MoveOrigin = e.Position; // re-anchor for the next segment
				effects.Add(new WorldTickEffect(e, moveType, target));
			}

			// #703: walks `e` along its harvested route - one waypoint at a time, in the order the route was captured in.
			//
			// This is the whole fix for NPCs cutting through buildings. A harvested route is a dense street-following
			// polyline (median 5.8u between waypoints), and the #266 wander threw that order away: it picked a RANDOM
			// waypoint and moved the entity there in one straight line, which in a city goes through the block rather
			// than round it. Walking the polyline in order keeps every step on ground the retail NPC actually walked,
			// with no collision data needed - the route IS the collision answer, sampled by whoever captured it.
			//
			// Movement is the same StepAndStream the chase uses (#272), so the client interpolates ~6u segments and the
			// server model stays where the client renders it. The waypoint's own Y is the target altitude, so a beat
			// that climbs a staircase climbs it instead of walking through the steps at the ground floor's height.
			void WalkRoute(WorldEntity e)
			{
				if (e.RouteInterrupted)
				{
					e.RejoinRouteAtNearest(); // chased or leashed off the beat: pick it up where it now stands
					e.RouteReissueDue = true; // ...and the client's copy of the walk is now wrong, so re-issue it
				}

				if (!e.TickRouteDwell(deltaSeconds))
					return; // standing at a turnaround; the client is already holding the same pose

				// #716: the beat is issued to the CLIENT as the captured multi-waypoint patrol and walked THERE - the
				// server does not stream it. Streaming it as 2-point chase segments cost 19.3 MoveTask/s against retail's
				// 3.09/s corpus maximum, made the NPC run (the chase form carries no duration, so the client paced it off
				// its run attribute), and tugged it back once per segment. Retail sends this form: of the 759 captured
				// multi-point patrol nodes, 535 carry [duration][remaining], where 423 of 425 two-point ones do not.
				if (e.RouteReissueDue)
				{
					e.RouteReissueDue = false;
					effects.Add(new WorldTickEffect(e, WorldTickEffectType.NpcPatrol));
				}

				e.RouteLapSeconds += deltaSeconds; // #714 probe: how long the model's lap ACTUALLY takes

				// #714: a viewer that just streamed this NPC in is told the beat a few seconds LATER, as retail does -
				// see RoutePatrolDelaySeconds. Sending it with the spawn made the client dash to reconcile.
				if (e.RoutePatrolDelayRemaining > 0)
				{
					e.RoutePatrolDelayRemaining -= deltaSeconds;
					if (e.RoutePatrolDelayRemaining <= 0)
					{
						e.RoutePatrolDelayRemaining = 0;
						e.RouteReissueDue = true;
					}
				}

				var waypoint = e.RouteWaypoint;
				var dx = waypoint.X - e.Position.X;
				var dz = waypoint.Z - e.Position.Z;
				var distance = MathF.Sqrt(dx * dx + dz * dz);
				if (distance > RouteWaypointReached)
				{
					// The MODEL only - no broadcast. It advances at the route's own captured speed so the server's idea of
					// where the NPC stands tracks what the client is rendering, which is what death-pose/loot/aggro need.
					StepToward(e, dx, dz, distance, deltaSeconds, 0f, waypoint.Y, e.RouteSpeed);
					return;
				}

				// Arrived. Land exactly on it (so the next leg starts from the polyline, not a fraction short of it) and
				// take the next waypoint; pause only where the route turns round or a lap closes - and re-issue the beat
				// there, since that is where the client's own copy of the walk has run out.
				e.Position = new Vector3(waypoint.X, waypoint.Y, waypoint.Z);
				if (e.AdvanceRoute())
				{
					e.ArmRouteDwell(RouteTurnaroundDwellSeconds);
					e.RouteReissueDue = true;
				}
			}

			// Ends `e`'s move (it reached its target / home / leader): flushes the trailing sub-segment the throttle didn't
			// broadcast (so the client walks the last bit instead of snapping) and, when `stop`, closes the stream with a
			// single StopIIR. The flush's segment direction also gives the stop a non-degenerate facing. No-op if `e`
			// wasn't moving, so an idle entity never re-emits a stop (#272).
			void EndMove(WorldEntity e, bool stop, WorldTickEffectType moveType)
			{
				if (!e.Moving)
					return;

				e.Moving = false;
				if (Vector3.DistanceSquared(e.MoveOrigin, e.Position) > 1e-4f)
				{
					e.MoveFrom = e.MoveOrigin;
					e.MoveOrigin = e.Position;
					effects.Add(new WorldTickEffect(e, moveType)); // final approach step (no target needed)
				}

				if (stop)
					effects.Add(new WorldTickEffect(e, WorldTickEffectType.NpcStopped));
			}
		}

		// Advances the staged VISUAL sequences running on static world objects (#678) and returns the steps that came
		// due - a harvested node's mesh swap, its collision drop, its regrowth.
		//
		// A separate pass from `Tick` because its subjects are separate: a `WorldTickEffect` is about a `WorldEntity`,
		// and a world object deliberately is not one (see Zone.WorldObjects). Squeezing an object into that record
		// would mean making its entity nullable for every existing effect.
		public IReadOnlyList<DueDynelVisualStep> TickVisualSequences(double deltaSeconds)
		{
			List<DueDynelVisualStep> steps = [];
			foreach (var zone in _zones.Values)
				steps.AddRange(zone.TickVisualSequences(deltaSeconds));

			return steps;
		}

		// Read-only lookup of the default shared instance; does NOT mint a zone for an untracked map.
		public bool TryGetZone(uint mapId, [MaybeNullWhen(false)] out Zone zone)
		{
			return _zones.TryGetValue((mapId, DefaultInstance), out zone);
		}

		// The default shared instance of a map (pre-instancing callers, tests, the burst-token resolve path).
		public Zone ZoneFor(uint mapId)
		{
			return ZoneFor(mapId, DefaultInstance);
		}

		// The (mapId, instanceId) zone, minting it (with a fallback wire token) if it doesn't exist yet (#268).
		public Zone ZoneFor(uint mapId, uint instanceId)
		{
			return _zones.GetOrAdd((mapId, instanceId), key => new Zone(key.MapId)
			{
				InstanceId = key.InstanceId,
				PlayfieldToken = NextPlayfieldToken()
			});
		}

		// The zone the entity currently lives in - its (MapId, InstanceId) copy. The routing primitive every
		// broadcast/interest query uses so it reaches only the player's own instance (#268).
		public Zone ZoneForEntity(WorldEntity entity)
		{
			return ZoneFor(entity.MapId, entity.InstanceId);
		}

		// Every live instance of a map (all copies), for observers (MCP) that want the whole map regardless of which
		// copy an entity is in.
		public IEnumerable<Zone> ZonesForMap(uint mapId)
		{
			foreach (var kvp in _zones)
			{
				if (kvp.Key.MapId == mapId)
					yield return kvp.Value;
			}
		}

		// The player this mob is already FIGHTING - whoever hit it last, while the combat memory holds - or null.
		//
		// This is the aggro source proximity cannot supply (#695): a mob attacked from beyond NpcAggroRange has nobody
		// "in range" to engage, so without this it stays disengaged no matter how hard it is hit. It deliberately
		// applies NONE of NearestPlayerInRange's gates - not range, not line of sight, not the vertical band - because
		// they all answer "would this mob NOTICE that player", and a mob that has been hit has already noticed. The
		// leash below is what bounds the resulting chase, exactly as it bounds a proximity chase.
		//
		// Still honours `Invisible`, which is the one gate that is not about noticing: `.invisible` means "draw no
		// aggro" by GM fiat, and a GM who turns it on mid-fight should stop being chased.
		private static WorldEntity? RecentAttackerOf(Zone zone, WorldEntity npc)
		{
			if (npc.LastAttackerHandle is not { } attacker || !npc.InCombatWithin(NpcCombatMemorySeconds))
				return null;

			return zone.TryGet(attacker, out var player) && player is { IsPlayer: true, Invisible: false }
				? player
				: null;
		}

		// The closest player within `range` of the NPC (its aggro target), or null if none.
		private WorldEntity? NearestPlayerInRange(Zone zone, WorldEntity npc, float range)
		{
			WorldEntity? nearest = null;
			var nearestDist = float.MaxValue;
			foreach (var player in zone.PlayersInRange(npc.Position, range))
			{
				if (player.Invisible) // .invisible: a hidden player draws no aggro
					continue;
				if (!npc.IsVisibleTo(player)) // a quest-gated NPC (e.g. Scavenger Tuut) must not aggro/attack a player it
					continue; // was never streamed to - else the player takes hits from an invisible body
				if (MathF.Abs(player.Position.Y - npc.Position.Y) > NpcAggroVerticalRange) // #284 tier 1: not through a floor/drop
					continue;

				var dist = Vector3.Distance(player.Position, npc.Position);
				// #284 tier 2/3: real occlusion - don't aggro a player behind a hill/ridge (terrain heightmap) or a
				// building wall (PhysX collision mesh). Skipped at point-blank range: a player standing on top of the NPC
				// must always aggro, so a thin triangle between two adjacent eye-points can't wedge the mob into never
				// attacking. Beyond that the line-of-sight gate fails OPEN when the assets are unavailable, so it only
				// ever REMOVES false aggros the range + tier-1 vertical gate would have wrongly allowed.
				if (dist > NpcMeleeRange && _terrain != null && !_terrain.HasLineOfSight(zone.MapId, npc.Position, player.Position))
					continue;

				if (dist < nearestDist)
				{
					nearestDist = dist;
					nearest = player;
				}
			}

			return nearest;
		}

		// A deterministic per-pet formation slot offset (X, Z) on a small ring around the leader, so multiple pets
		// following one player fan out instead of piling on the same spot (#307). Derived from the entity handle so
		// it is STABLE across ticks (no per-tick jitter that would make the pet jitter in place): the handle picks an
		// angle around the circle and one of a few radius rings (so more than a ring's worth of pets don't collide).
		private static (float X, float Z) FollowFormationOffset(uint handle)
		{
			var angle = handle % 3600 * (MathF.PI / 1800f); // 0..2π in 0.1° steps, deterministic per handle
			var radius = 3f + handle % 3; // 3 / 4 / 5u rings
			return (radius * MathF.Cos(angle), radius * MathF.Sin(angle));
		}

		// Advances `entity` one tick's worth toward (dx, dz) - a horizontal step at `speed` (keeping terrain Y),
		// `stopShort` leaves that many units short of the point (melee range when chasing, follow range when following);
		// 0 lands on it (returning home). The broadcast anchor (MoveOrigin) is managed by StepAndStream, not here, so a
		// caller that wants the step broadcast must go through StepAndStream.
		// `targetY` (#565) closes the vertical gap over the same walk instead of keeping the current Y: the return home
		// knows the altitude it is walking to (the spawn's), so it descends smoothly rather than arriving and snapping.
		// `speed` defaults to NpcMoveSpeed, the RUN a chase/return uses; an idle route walk passes its own captured
		// speed instead (#714), because a beat is walked and the two are not the same number.
		private static void StepToward(WorldEntity entity, float dx, float dz, float distance, double deltaSeconds, float stopShort = 0f, float? targetY = null,
			float speed = NpcMoveSpeed)
		{
			var travel = MathF.Min((float)(speed * deltaSeconds), distance - stopShort);
			var y = entity.Position.Y;
			if (targetY is { } destY)
			{
				// Close the same FRACTION of the height gap as of the horizontal distance, so both land together.
				var remaining = distance - stopShort;
				y += (destY - y) * (remaining > 1e-4f ? MathF.Min(1f, travel / remaining) : 1f);
			}

			entity.Position = new Vector3(entity.Position.X + dx / distance * travel, y, entity.Position.Z + dz / distance * travel);
		}
	}
}
