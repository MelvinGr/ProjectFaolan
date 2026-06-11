using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;

namespace Faolan.GameServer.World
{
	// The live world: one Zone per map, created on demand. The authoritative in-memory model the GameServer simulation
	// is built on (docs/server-architecture-roadmap.md). A single instance is shared across the listener; entities are
	// placed when a player/NPC enters a map and removed on disconnect/despawn. Map changes move an entity between zones.
	public sealed class WorldState
	{
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

		public Zone ZoneFor(uint mapId)
		{
			return _zones.GetOrAdd(mapId, m => new Zone(m) { PlayfieldToken = NextPlayfieldToken() });
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

		// Advances the world model by `deltaSeconds` (docs/server-architecture-roadmap.md). Today it runs the NPC
		// respawn timers: any dead NPC whose respawn delay elapsed this tick is revived (vitals + transform reset) and
		// returned so the caller can re-spawn it for nearby players. Player transforms are driven by Move packets, not
		// the tick; NPC AI/movement joins here later.
		public IReadOnlyList<WorldEntity> Tick(double deltaSeconds)
		{
			List<WorldEntity> respawned = null;
			foreach (var zone in _zones.Values)
			{
				foreach (var entity in zone.Entities)
				{
					if (!entity.IsPlayer && entity.TickRespawn(deltaSeconds))
						(respawned ??= new List<WorldEntity>()).Add(entity);
				}
			}

			return respawned ?? (IReadOnlyList<WorldEntity>)System.Array.Empty<WorldEntity>();
		}
	}
}
