using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Faolan.GameServer.World
{
	// One map's live entity registry: every player and NPC currently in that map, keyed by wire handle. Provides the
	// spatial queries the broadcaster needs (who is near a point) so movement/spawns reach only nearby players -
	// interest management (docs/server-architecture-roadmap.md). Range scans are linear for now (correct + simple at
	// beach scale); a uniform grid is the optimisation once entity counts grow.
	public sealed class Zone
	{
		private readonly ConcurrentDictionary<uint, WorldEntity> _entities = new ConcurrentDictionary<uint, WorldEntity>();

		public Zone(uint mapId)
		{
			MapId = mapId;
		}

		public uint MapId { get; }

		// The per-playfield-instance token the client associated with this zone at world entry (the value woven into
		// the 0x737A6DF9 snapshot tail; see WorldDespawn). Stamped into every despawn so the client scopes the removal
		// to this playfield. Extracted from the burst we actually send (WorldDespawn.TryExtractPlayfieldToken); 0 until
		// known, in which case the broadcaster skips the despawn rather than guess.
		public uint PlayfieldToken { get; set; }

		public int Count => _entities.Count;

		public IReadOnlyCollection<WorldEntity> Entities => _entities.Values.ToArray();

		public void Add(WorldEntity entity)
		{
			_entities[entity.Handle] = entity;
		}

		public bool Remove(uint handle)
		{
			return _entities.TryRemove(handle, out _);
		}

		public bool TryGet(uint handle, out WorldEntity entity)
		{
			return _entities.TryGetValue(handle, out entity);
		}

		// Entities within `range` of `position` (3D), excluding `excludeHandle` (typically the source itself).
		public IEnumerable<WorldEntity> EntitiesInRange(Vector3 position, float range, uint? excludeHandle = null)
		{
			foreach (var entity in _entities.Values)
			{
				if (excludeHandle.HasValue && entity.Handle == excludeHandle.Value)
					continue;
				if (Vector3.Distance(entity.Position, position) <= range)
					yield return entity;
			}
		}

		// Players within `range` of `position` - the broadcast recipients for a move/spawn/say/etc.
		public IEnumerable<WorldEntity> PlayersInRange(Vector3 position, float range, uint? excludeHandle = null)
		{
			return EntitiesInRange(position, range, excludeHandle).Where(e => e.IsPlayer);
		}
	}
}
