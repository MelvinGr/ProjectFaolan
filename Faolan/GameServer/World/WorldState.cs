using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;

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

		// How fast a chasing NPC closes on its target (world units per second). A run-pace value so it keeps up with a
		// fleeing player (a slower value rubber-bands) and the client reads the faster movement as a run, not a walk.
		public const float NpcMoveSpeed = 12f;

		// How far (horizontal) an NPC will chase from its spawn before giving up and returning home (so a player can't
		// drag a mob across the whole zone). Once it disengages it keeps returning until home, ignoring re-aggro.
		public const float NpcLeashRange = 40f;
		private readonly ConcurrentDictionary<uint, Zone> _zones = new ConcurrentDictionary<uint, Zone>();

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

		// Adds the entity to `mapId`'s zone with the given transform, removing it from its previous zone if its map
		// changed. Returns the zone it now lives in so the caller can run interest queries against it.
		public Zone Place(WorldEntity entity, uint mapId, Vector3 position, Vector3 rotation)
		{
			if (entity.MapId != mapId && _zones.TryGetValue(entity.MapId, out var oldZone))
				oldZone.Remove(entity.Handle);

			entity.MapId = mapId;
			entity.Position = position;
			entity.Rotation = rotation;
			var zone = ZoneFor(mapId);
			zone.Add(entity);
			return zone;
		}

		// Removes the entity from its current map's zone (disconnect / despawn).
		public void Remove(WorldEntity entity)
		{
			if (_zones.TryGetValue(entity.MapId, out var zone))
				zone.Remove(entity.Handle);
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
						if (entity.TickRespawn(deltaSeconds))
							effects.Add(new WorldTickEffect(entity, WorldTickEffectType.NpcRespawned));
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

					if (entity.Returning)
					{
						SetCombat(entity, false); // disengaged: sheathe the weapon while walking home
						entity.ArmAttackCooldown(NpcAttackInterval);
						if (homeDist > NpcMeleeRange)
						{
							StepToward(entity, homeDx, homeDz, homeDist, deltaSeconds);
							effects.Add(new WorldTickEffect(entity, WorldTickEffectType.NpcMove));
							continue;
						}

						entity.Returning = false;
						entity.Vitals.ReviveToFull(); // home again
						continue;
					}

					if (target == null)
					{
						SetCombat(entity, false); // no target in range: disengage and sheathe
						entity.ArmAttackCooldown(NpcAttackInterval); // re-aggro shouldn't swing instantly
						// Disengaged with no one in range: heal back to full so it can't be whittled from out of range.
						if (entity.Vitals.Health < entity.Vitals.MaxHealth)
							entity.Vitals.ReviveToFull();
						continue;
					}

					// Engaged: a target is in aggro range and we're not leashing home. Draw the weapon on the transition
					// (the captured combat-mode stat) BEFORE the chase/swing, mirroring retail - the stat-20 update lands
					// the tick the mob acquires its target, while it's still closing the distance (#218).
					SetCombat(entity, true);

					// Chase: if the target is beyond melee range, step toward it (horizontal plane; keep terrain Y) and
					// broadcast the move instead of swinging this tick. The client interpolates origin->position, so a
					// 10 Hz step reads as the NPC running up to the player; it swings once it arrives in melee range.
					var dx = target.Position.X - entity.Position.X;
					var dz = target.Position.Z - entity.Position.Z;
					var horizontal = MathF.Sqrt(dx * dx + dz * dz);
					if (horizontal > NpcMeleeRange)
					{
						StepToward(entity, dx, dz, horizontal, deltaSeconds, leaveMelee: true);
						entity.ArmAttackCooldown(NpcAttackInterval); // wind up a swing for when it arrives
						effects.Add(new WorldTickEffect(entity, WorldTickEffectType.NpcMove, target));
						continue;
					}

					if (entity.TickAttackReady(deltaSeconds, NpcAttackInterval))
						effects.Add(new WorldTickEffect(entity, WorldTickEffectType.NpcAttack, target));
				}
			}

			return effects;

			// Flips an NPC's combat-engaged state and, on the transition, queues the combat-mode effect the broadcaster
			// turns into the stat-20 weapon draw/sheathe (#218). No-op when already in that state, so it fires once per
			// engage/disengage.
			void SetCombat(WorldEntity e, bool inCombat)
			{
				if (e.InCombat == inCombat)
					return;

				e.InCombat = inCombat;
				effects.Add(new WorldTickEffect(e,
					inCombat ? WorldTickEffectType.NpcEnterCombat : WorldTickEffectType.NpcExitCombat));
			}
		}

		public Zone ZoneFor(uint mapId)
		{
			return _zones.GetOrAdd(mapId, m => new Zone(m) { PlayfieldToken = NextPlayfieldToken() });
		}

		// Read-only zone lookup that does NOT mint a zone for an untracked map (for observers like the MCP tools).
		public bool TryGetZone(uint mapId, out Zone zone)
		{
			return _zones.TryGetValue(mapId, out zone);
		}

		// Advances `entity` one tick's worth toward (dx, dz) - a horizontal step at NpcMoveSpeed (keeping terrain Y),
		// recording the origin so the broadcast can send origin->position for the client to interpolate. `leaveMelee`
		// stops one melee-range short (chasing a target) vs landing on the point (returning home).
		private static void StepToward(WorldEntity entity, float dx, float dz, float distance, double deltaSeconds, bool leaveMelee = false)
		{
			var travel = MathF.Min((float)(NpcMoveSpeed * deltaSeconds), leaveMelee ? distance - NpcMeleeRange : distance);
			entity.MoveOrigin = entity.Position;
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
