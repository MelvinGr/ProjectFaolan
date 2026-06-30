using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using Faolan.Core.Database.Tables;

namespace Faolan.GameServer.World
{
	// The live world: one Zone per map, created on demand. The authoritative in-memory model the GameServer simulation
	// is built on (docs/archive/server-architecture-roadmap.md). A single instance is shared across the listener; entities are
	// placed when a player/NPC enters a map and removed on disconnect/despawn. Map changes move an entity between zones.
	public sealed class WorldState
	{
		// Seconds between an aggroed NPC's swings.
		public const double NpcAttackInterval = 2.0;

		// How close a player must be for a hostile NPC to aggro + attack (world units).
		public const float NpcAggroRange = 20f;

		// How close (horizontal) an aggroed NPC must be to its target to swing; beyond this it chases.
		public const float NpcMeleeRange = 3f;

		// #272: how close a chasing NPC actually WALKS UP to its target (well inside NpcMeleeRange so it reaches contact
		// and stays comfortably in swing range, not hovering exactly on the swing boundary where a drifting target leaves
		// it perpetually a step away and never swinging). Must be < NpcMeleeRange.
		public const float NpcContactRange = 1.5f;

		// How fast a chasing NPC closes on its target (world units per second). Capture-measured: retail renders NPC
		// locomotion at ~6.9 u/s (median over 207 clear-locomotion samples, mode 7; Captures movement trajectories). This
		// MUST equal the speed the CLIENT renders the NPC at, because the client paces a no-duration 2-point MoveTask
		// segment by the entity's movement speed, not by the packet: if the model steps faster than the client renders
		// (the old 12 u/s), the model outruns the on-screen NPC and each new segment makes it jump forward = the #272
		// chase teleport. 7 matches the render speed so consecutive segments chain smoothly. (Also ~PatrolWalkSpeed 6.)
		public const float NpcMoveSpeed = 7f;

		// How far (horizontal) an NPC will chase from its spawn before giving up and returning home (so a player can't
		// drag a mob across the whole zone). Once it disengages it keeps returning until home, ignoring re-aggro.
		public const float NpcLeashRange = 40f;

		// #262 companion-follow: how far behind the leading player a follower trails before it steps up (retail keeps the
		// follower ~one step behind; its MoveTasks track the player ~6-7u apart). A follower never leashes/aggros.
		public const float FollowStopRange = 4f;

		// #272 smooth movement: the client interpolates a MoveTask's from->to segment over the implied time, so the server
		// must feed it retail-sized segments - retail's chase/follow stream is ~6-7u steps. The model still steps Position
		// every tick (combat/interest fidelity), but a MoveTask is only broadcast once the entity has travelled this far
		// since the last one; a 1.2u-per-tick (10 Hz) MoveTask flood is what the client can't interpolate -> the jitter/
		// teleport (#272). At NpcMoveSpeed (7 u/s) this broadcasts ~once a second.
		public const float MoveBroadcastSegment = 6f;

		// The live zones keyed by (mapId, instanceId) (#268). A map can have several copies (instances): the default
		// shared instance plus any per-character Solo / per-party Group copies. Instance DefaultInstance (0) is the
		// shared copy every Shared map uses - so ZoneFor(mapId) / the historical single-zone-per-map paths all resolve
		// to it and behave exactly as before instancing existed.
		private readonly ConcurrentDictionary<(uint MapId, uint InstanceId), Zone> _zones = new ConcurrentDictionary<(uint MapId, uint InstanceId), Zone>();

		// The shared instance id every Shared map (and every pre-instancing call site) uses - one zone per map.
		public const uint DefaultInstance = 0;

		// Server-global monotonic allocator for playfield-instance tokens, mirroring retail's behaviour (a steadily
		// increasing per-instance id stamped into despawns; see protocol_despawn_n3toclientquit). Every Zone gets a
		// fresh token on creation as a FALLBACK. For a REPLAYED playfield the burst's own token then overrides it (the
		// client learned that one from the burst - WorldDespawn.TryExtractPlayfieldToken). This minted value becomes
		// the AUTHORITATIVE source once we GENERATE playfields (construct-don't-replay) and write the token into the
		// generated entry packets ourselves. Seeded clear of the observed retail pools (~0x004F.. / ~0x01EA..) so a
		// minted token is recognisably ours in a capture.
		private uint _nextPlayfieldToken = 0x00A00000;

		// Mints the next playfield-instance token. Thread-safe (zones can be created from any connection).
		public uint NextPlayfieldToken()
		{
			return Interlocked.Increment(ref _nextPlayfieldToken);
		}

		// Adds the entity to the default shared instance of `mapId` (pre-instancing callers / tests). See the
		// instance-aware overload.
		public Zone Place(WorldEntity entity, uint mapId, Vector3 position, Vector3 rotation)
		{
			return Place(entity, mapId, DefaultInstance, position, rotation);
		}

		// Adds the entity to the (mapId, instanceId) zone with the given transform, removing it from its previous zone
		// if its map OR instance changed (#268). Returns the zone it now lives in so the caller can run interest
		// queries against it. The entity's MapId/InstanceId are updated to match - so every later ZoneForEntity lands
		// in this same copy.
		public Zone Place(WorldEntity entity, uint mapId, uint instanceId, Vector3 position, Vector3 rotation)
		{
			if ((entity.MapId != mapId || entity.InstanceId != instanceId)
			    && _zones.TryGetValue((entity.MapId, entity.InstanceId), out var oldZone))
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
		public uint ResolveEntryInstance(WorldEntity existing, uint mapId, MapInstanceMode mode)
		{
			if (existing != null && existing.MapId == mapId && existing.InstanceId != DefaultInstance)
				return existing.InstanceId; // already in a private copy of this same map - stay in it

			return mode == MapInstanceMode.Solo ? NextPlayfieldToken() : DefaultInstance;
		}

		// Advances the world model by `deltaSeconds` (docs/archive/server-architecture-roadmap.md) and returns the wire-visible
		// effects for the caller to broadcast: dead NPCs whose respawn delay elapsed (revived here - vitals + transform
		// reset), loot bags whose lifetime expired (despawned + removed), and aggroed NPCs swinging at a nearby player.
		// Player transforms are driven by Move packets, not the tick; aggroed NPCs chase here (NpcMove effects, broadcast
		// as MoveTask). The model stays network-free - it reports effects and the listener turns them into packets.
		public IReadOnlyList<WorldTickEffect> Tick(double deltaSeconds)
		{
			List<WorldTickEffect> effects = [];

			foreach (var zone in _zones.Values) // Parralell?
			{
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

					// A dead NPC first lingers as the dying creature (death animation), then its corpse becomes a
					// lootable bag, then it respawns. The corpse-linger and respawn timers run independently off the
					// same death; the linger (~3s) elapses well before the respawn (~15s) so the bag is lootable in
					// between.
					if (entity.IsDead)
					{
						if (entity.TickCorpseLinger(deltaSeconds))
							effects.Add(new WorldTickEffect(entity, WorldTickEffectType.NpcCorpseLootable));
						// Summoned (.spawnnpc) NPCs don't revive: once the post-death timer elapses, fade + remove the
						// corpse (LootExpired) instead of respawning it.
						if (entity.NoRespawn)
						{
							if (entity.TickPostDeathDespawn(deltaSeconds))
								effects.Add(new WorldTickEffect(entity, WorldTickEffectType.LootExpired));
						}
						else if (entity.TickRespawn(deltaSeconds))
							effects.Add(new WorldTickEffect(entity, WorldTickEffectType.NpcRespawned));
						continue;
					}

					// #262 companion-follow: a "released" NPC (e.g. Casilda after her rescue) follows its leading player -
					// step toward them and broadcast a MoveTask each step (UNGATED; not combat), the server-driven
					// incremental follow retail uses. Takes priority over patrol/aggro/leash. Clears when the leader
					// leaves the zone or logs out.
					if (entity.Following)
					{
						if (!zone.TryGet(entity.FollowTargetHandle, out var leader) || !leader.IsPlayer)
						{
							entity.FollowTargetHandle = 0;
							EndMove(entity, stop: true, WorldTickEffectType.NpcFollow);
							continue;
						}

						var fdx = leader.Position.X - entity.Position.X;
						var fdz = leader.Position.Z - entity.Position.Z;
						var fdist = MathF.Sqrt(fdx * fdx + fdz * fdz);
						if (fdist > FollowStopRange)
							StepAndStream(entity, fdx, fdz, fdist, FollowStopRange, WorldTickEffectType.NpcFollow, leader);
						else
							// Caught up: flush the trailing approach (a real ~6u segment toward the leader, so its facing
							// points straight at her) and close the move with a single StopIIR (#262/#272).
							EndMove(entity, stop: true, WorldTickEffectType.NpcFollow);
						continue;
					}

					if (entity.Vitals == null)
						continue;

					// Only hostile NPCs (mobs) aggro; talk-to NPCs (quest-givers/vendors) never attack.
					var target = entity.Hostile
						? NearestPlayerInRange(zone, entity, NpcAggroRange)
						: null;

					var homeDx = entity.SpawnPosition.X - entity.Position.X;
					var homeDz = entity.SpawnPosition.Z - entity.Position.Z;
					var homeDist = MathF.Sqrt(homeDx * homeDx + homeDz * homeDz);

					// Leash: once led too far from spawn, disengage and keep returning home until arrived (ignoring
					// re-aggro) so a player hovering at the edge can't ping-pong it. A returning NPC walks back, then
					// heals to full on arrival.
					if (homeDist > NpcLeashRange)
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
						if (homeDist > NpcMeleeRange)
						{
							StepAndStream(entity, homeDx, homeDz, homeDist, 0f, WorldTickEffectType.NpcMove, null);
							continue;
						}

						entity.Returning = false;
						entity.Vitals.ReviveToFull(); // home again
						EndMove(entity, stop: true, WorldTickEffectType.NpcMove); // arrived: flush + stop (#190/#272)
						continue;
					}

					if (target == null)
					{
						SetCombat(entity, false); // no target in range: disengage and sheathe
						entity.ArmAttackCooldown(NpcAttackInterval); // re-aggro shouldn't swing instantly
						EndMove(entity, stop: true, WorldTickEffectType.NpcMove); // stop if we were mid-chase (#272)
						// Disengaged with no one in range: heal back to full so it can't be whittled from out of range.
						if (entity.Vitals.Health < entity.Vitals.MaxHealth)
							entity.Vitals.ReviveToFull();
						// #266: idle + at home -> keep its captured patrol going. The client walks the route; when a cycle
						// elapses (path traversal + dwell) re-issue it so it loops (retail re-paths idle patrols).
						if (entity.Patrols && entity.TickPatrolCycle(deltaSeconds))
						{
							entity.ArmPatrolCycle();
							effects.Add(new WorldTickEffect(entity, WorldTickEffectType.NpcPatrol));
						}
						continue;
					}

					// Engaged: a target is in aggro range and we're not leashing home. Draw the weapon on the transition
					// (the captured combat-mode stat) BEFORE the chase/swing, mirroring retail - the stat-20 update lands
					// the tick the mob acquires its target, while it's still closing the distance (#218). The target rides
					// the effect so the broadcaster can also push the aggro (HateList/ThreatLevel, #216).
					SetCombat(entity, true, target);

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
						EndMove(entity, stop: false, WorldTickEffectType.NpcMove);

					if (horizontal > NpcMeleeRange)
					{
						entity.ArmAttackCooldown(NpcAttackInterval); // still out of swing range: wind up for arrival
						continue;
					}

					if (entity.TickAttackReady(deltaSeconds, NpcAttackInterval))
						// A caster NPC casts a spell on the same cadence; everyone else swings (#232).
						effects.Add(new WorldTickEffect(entity, entity.IsCaster ? WorldTickEffectType.NpcCast : WorldTickEffectType.NpcAttack, target));
				}
			}

			return effects;

			// Flips an NPC's combat-engaged state and, on the transition, queues the combat-mode effect the broadcaster
			// turns into the stat-20 weapon draw/sheathe (#218). No-op when already in that state, so it fires once per
			// engage/disengage.
			void SetCombat(WorldEntity e, bool inCombat, WorldEntity target = null)
			{
				if (e.InCombat == inCombat)
					return;

				e.InCombat = inCombat;
				effects.Add(new WorldTickEffect(e,
					inCombat ? WorldTickEffectType.NpcEnterCombat : WorldTickEffectType.NpcExitCombat, target));
			}

			// Steps `e` toward (dx,dz) this tick and queues a move broadcast ONLY once it has travelled a retail-sized
			// segment since the last one (#272), so the client interpolates ~6u segments instead of a per-tick snap. The
			// model position advances every call regardless; `moveType` is NpcMove (chase/return) or NpcFollow (companion)
			// so the loop gates/handles it correctly. `target` rides the effect (the aggro/leader the broadcaster wants).
			void StepAndStream(WorldEntity e, float dx, float dz, float dist, float stopShort, WorldTickEffectType moveType, WorldEntity target)
			{
				if (!e.Moving) // a fresh move: anchor the first segment at the start point
				{
					e.MoveOrigin = e.Position;
					e.Moving = true;
				}

				StepToward(e, dx, dz, dist, deltaSeconds, stopShort);
				if (Vector3.DistanceSquared(e.MoveOrigin, e.Position) < MoveBroadcastSegment * MoveBroadcastSegment)
					return; // segment too short to broadcast yet - keep accumulating

				e.MoveFrom = e.MoveOrigin; // the segment the broadcaster sends (MoveFrom -> Position)
				e.MoveOrigin = e.Position; // re-anchor for the next segment
				effects.Add(new WorldTickEffect(e, moveType, target));
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

		// The default shared instance of a map (pre-instancing callers, tests, the burst-token resolve path).
		public Zone ZoneFor(uint mapId)
		{
			return ZoneFor(mapId, DefaultInstance);
		}

		// The (mapId, instanceId) zone, minting it (with a fallback wire token) if it doesn't exist yet (#268).
		public Zone ZoneFor(uint mapId, uint instanceId)
		{
			return _zones.GetOrAdd((mapId, instanceId),
				key => new Zone(key.MapId) { InstanceId = key.InstanceId, PlayfieldToken = NextPlayfieldToken() });
		}

		// The zone the entity currently lives in - its (MapId, InstanceId) copy. The routing primitive every
		// broadcast/interest query uses so it reaches only the player's own instance (#268).
		public Zone ZoneForEntity(WorldEntity entity)
		{
			return ZoneFor(entity.MapId, entity.InstanceId);
		}

		// Read-only lookup of the default shared instance; does NOT mint a zone for an untracked map.
		public bool TryGetZone(uint mapId, out Zone zone)
		{
			return _zones.TryGetValue((mapId, DefaultInstance), out zone);
		}

		// Every live instance of a map (all copies), for observers (MCP) that want the whole map regardless of which
		// copy an entity is in.
		public IEnumerable<Zone> ZonesForMap(uint mapId)
		{
			foreach (var kvp in _zones)
				if (kvp.Key.MapId == mapId)
					yield return kvp.Value;
		}

		// Advances `entity` one tick's worth toward (dx, dz) - a horizontal step at NpcMoveSpeed (keeping terrain Y),
		// `stopShort` leaves that many units short of the point (melee range when chasing, follow range when following);
		// 0 lands on it (returning home). The broadcast anchor (MoveOrigin) is managed by StepAndStream, not here, so a
		// caller that wants the step broadcast must go through StepAndStream.
		private static void StepToward(WorldEntity entity, float dx, float dz, float distance, double deltaSeconds, float stopShort = 0f)
		{
			var travel = MathF.Min((float)(NpcMoveSpeed * deltaSeconds), distance - stopShort);
			entity.Position = new Vector3(
				entity.Position.X + dx / distance * travel,
				entity.Position.Y,
				entity.Position.Z + dz / distance * travel);
		}

		// The closest player within `range` of the NPC (its aggro target), or null if none.
		private static WorldEntity NearestPlayerInRange(Zone zone, WorldEntity npc, float range)
		{
			WorldEntity nearest = null;
			var nearestDist = float.MaxValue;
			foreach (var player in zone.PlayersInRange(npc.Position, range))
			{
				if (player.Invisible) // .invisible: a hidden player draws no aggro
					continue;
				if (!npc.IsVisibleTo(player)) // a quest-gated NPC (e.g. Scavenger Tuut) must not aggro/attack a player it
					continue;                 // was never streamed to - else the player takes hits from an invisible body

				var dist = Vector3.Distance(player.Position, npc.Position);
				if (dist < nearestDist)
				{
					nearestDist = dist;
					nearest = player;
				}
			}

			return nearest;
		}
	}
}
