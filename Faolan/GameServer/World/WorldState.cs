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

		// How fast a chasing NPC closes on its target (world units per second).
		public const float NpcMoveSpeed = 6f;
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
			List<WorldTickEffect> effects = null;
			foreach (var zone in _zones.Values)
			{
				foreach (var entity in zone.Entities)
				{
					if (entity.IsLoot)
					{
						if (entity.TickExpiry(deltaSeconds))
							(effects ??= new List<WorldTickEffect>()).Add(new WorldTickEffect(entity, WorldTickEffectType.LootExpired));
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
							(effects ??= new List<WorldTickEffect>()).Add(new WorldTickEffect(entity, WorldTickEffectType.NpcCorpseLootable));
						if (entity.TickRespawn(deltaSeconds))
							(effects ??= new List<WorldTickEffect>()).Add(new WorldTickEffect(entity, WorldTickEffectType.NpcRespawned));
						continue;
					}

					if (entity.Vitals == null)
						continue;

					// Only hostile NPCs (mobs) aggro; talk-to NPCs (quest-givers/vendors) never attack.
					var target = entity.Hostile
						? NearestPlayerInRange(zone, entity, NpcAggroRange)
						: null;
					if (target == null)
					{
						entity.ArmAttackCooldown(NpcAttackInterval); // re-aggro shouldn't swing instantly
						// Leash: a damaged NPC with no one in range has disengaged - heal back to full (so it can't be
						// whittled down from out of range / across separate pulls). Mobs don't move yet, so "return home"
						// is just the HP reset.
						if (entity.Vitals.Health < entity.Vitals.MaxHealth)
							entity.Vitals.ReviveToFull();
						continue;
					}

					// Chase: if the target is beyond melee range, step toward it (horizontal plane; keep terrain Y) and
					// broadcast the move instead of swinging this tick. The client interpolates origin->position, so a
					// 10 Hz step reads as the NPC walking up to the player; it swings once it arrives in melee range.
					var dx = target.Position.X - entity.Position.X;
					var dz = target.Position.Z - entity.Position.Z;
					var horizontal = MathF.Sqrt(dx * dx + dz * dz);
					if (horizontal > NpcMeleeRange)
					{
						var step = MathF.Min((float)(NpcMoveSpeed * deltaSeconds), horizontal - NpcMeleeRange);
						entity.MoveOrigin = entity.Position;
						entity.Position = new Vector3(entity.Position.X + dx / horizontal * step, entity.Position.Y, entity.Position.Z + dz / horizontal * step);
						entity.ArmAttackCooldown(NpcAttackInterval); // wind up a swing for when it arrives
						(effects ??= new List<WorldTickEffect>()).Add(new WorldTickEffect(entity, WorldTickEffectType.NpcMove, target));
						continue;
					}

					if (entity.TickAttackReady(deltaSeconds, NpcAttackInterval))
						(effects ??= new List<WorldTickEffect>()).Add(new WorldTickEffect(entity, WorldTickEffectType.NpcAttack, target));
				}
			}

			return effects ?? (IReadOnlyList<WorldTickEffect>)Array.Empty<WorldTickEffect>();
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
