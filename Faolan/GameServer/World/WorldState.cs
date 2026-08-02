using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
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

		// How close a player must be for a hostile NPC to aggro + attack (world units).
		public const float NpcAggroRange = 20f;

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

		// The idle-heartbeat cadence (#225): every live NPC (re-)asserts its short-form BehaviorControlCenterIIR this
		// often so the client keeps playing its idle animation instead of freezing. Retail streams it faster (~10/s
		// aggregate in the mount capture; the per-entity rate isn't pinned from captures), so this is a deliberately
		// conservative, single-place-tunable interval - dial it in the in-client pass. The broadcast is gated to
		// Confirmed viewers, so it never fires at a still-loading client (the hang the earlier experiment hit).
		private const double HeartbeatIntervalSeconds = 1.0;

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

			// #225: fire the idle heartbeat for every live NPC on its own cadence (once per HeartbeatIntervalSeconds),
			// not every base tick. Computed once per Tick, then applied inside the entity loop below.
			var heartbeatDue = _heartbeat.Advance(deltaSeconds, out _);
			var chatterDue = _chatter.Advance(deltaSeconds, out _);

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
					if (heartbeatDue)
						effects.Add(new WorldTickEffect(entity, WorldTickEffectType.NpcHeartbeat));

					// Ambient chatter: NPCs that have captured bark lines (NpcChatter) occasionally speak, staggered by
					// the per-NPC random roll so they don't all bark at once. The processor picks a random line + sends
					// it to nearby players (cosmetic). NPCs without a captured group stay silent.
					if (chatterDue && entity.ChatterLines is { Length: > 0 } && Random.Shared.NextDouble() < ChatterChancePerCheck)
						effects.Add(new WorldTickEffect(entity, WorldTickEffectType.NpcChatter));

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
					var target = entity.Hostile
						? NearestPlayerInRange(zone, entity, NpcAggroRange)
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
						EndMove(entity, true, WorldTickEffectType.NpcMove); // stop if we were mid-chase (#272)
						// Disengaged with no one in range: heal back to full so it can't be whittled from out of range.
						if (entity.Vitals.Health < entity.Vitals.MaxHealth)
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

		// Advances `entity` one tick's worth toward (dx, dz) - a horizontal step at NpcMoveSpeed (keeping terrain Y),
		// `stopShort` leaves that many units short of the point (melee range when chasing, follow range when following);
		// 0 lands on it (returning home). The broadcast anchor (MoveOrigin) is managed by StepAndStream, not here, so a
		// caller that wants the step broadcast must go through StepAndStream.
		// `targetY` (#565) closes the vertical gap over the same walk instead of keeping the current Y: the return home
		// knows the altitude it is walking to (the spawn's), so it descends smoothly rather than arriving and snapping.
		private static void StepToward(WorldEntity entity, float dx, float dz, float distance, double deltaSeconds, float stopShort = 0f, float? targetY = null)
		{
			var travel = MathF.Min((float)(NpcMoveSpeed * deltaSeconds), distance - stopShort);
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
