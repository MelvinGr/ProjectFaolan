using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using System.Threading;

namespace Faolan.GameServer.World
{
	// One map's live entity registry: every player and NPC currently in that map, keyed by wire handle. Provides the
	// spatial queries the broadcaster needs (who is near a point) so movement/spawns reach only nearby players -
	// interest management (Gitea #404). Range scans are linear for now (correct + simple at
	// beach scale); a uniform grid is the optimisation once entity counts grow.
	public sealed class Zone
	{
		private readonly ConcurrentDictionary<uint, WorldEntity> _entities = new ConcurrentDictionary<uint, WorldEntity>();

		// Which copy (instance) of MapId this zone is (#268). Together with MapId it is the zone's identity key in
		// WorldState. 0 = the default shared instance (the historical single-zone-per-map behaviour); a Solo/Group
		// map mints a non-zero id per copy. Distinct from PlayfieldToken (the WIRE token below): the InstanceId is
		// the stable routing key, the PlayfieldToken is the value the client learned from the burst (which, until
		// per-instance burst stamping lands - #268 step 4 - is shared across a Solo map's copies, harmlessly, since a
		// client only ever sees its own instance).
		public uint InstanceId { get; set; }

		// #309: whether this copy is the map's EPIC difficulty. Stamped at entry from the player's travel-box pick;
		// what it changes today is only ROUTING (an epic copy never shares with a normal one) and the MapChange's
		// class word (0xCABC) - the content is deliberately the normal map's until the epic tuning is modelled
		// (measured so far: ~8x health on non-minion mobs, see NpcSpawnExtractor's epic-window note).
		public bool IsEpic { get; set; }

		private uint _nextHandle;

		public uint MapId { get; }

		public IReadOnlyCollection<WorldEntity> Entities => _entities.Values.ToArray();

		public int Count => _entities.Count;

		// The per-playfield-instance token stamped into every despawn so the client scopes the removal to this playfield
		// (see N3ToClientQuitCodec). Minted by WorldState on zone creation as a fallback; for a REPLAYED playfield the burst's
		// own token (the value woven into the 0x737A6DF9 snapshot tail, the one the client actually learned) overrides
		// it via PlayfieldTokenScan.TryExtract.
		public uint PlayfieldToken { get; set; }

		// The map's STATIC world objects (props, doors, climbing vines, destructibles), loaded once per zone by
		// ZonePopulationLoader.EnsureZoneWorldObjects rather than re-queried per world entry (#424). They are deliberately
		// NOT WorldEntities: they do not move, think, fight or tick, so putting ~1000 inert props in _entities would
		// buy nothing and cost the simulation a pass over all of them. What they DO need is interest streaming, which
		// is why they live on the zone - see WorldObjectsInRange.
		//
		// A loot bag is the exception and IS a WorldEntity: it has a lifecycle (open, expire, fade), which is exactly
		// the state that earns entity-hood.
		public IReadOnlyList<LiveWorldObject> WorldObjects { get; set; } = [];

		// The staged visual sequences currently running on this zone's objects (#678) - a harvested node fading out,
		// swapping its mesh, and growing back. Keyed by handle so a second harvest of the same node REPLACES its
		// sequence instead of interleaving two of them.
		private readonly ConcurrentDictionary<uint, DynelVisualSequence> _visualSequences = new ConcurrentDictionary<uint, DynelVisualSequence>();

		public Zone(uint mapId)
		{
			MapId = mapId;
		}

		// Starts (or restarts) an object's harvest visual sequence. The caller has already sent its opening fade-out;
		// this schedules everything that follows it.
		public void StartVisualSequence(LiveWorldObject obj)
		{
			_visualSequences[obj.Handle] = new DynelVisualSequence(obj);
		}

		// Advances every running sequence by `deltaSeconds` and yields the steps that came due.
		public IEnumerable<DueDynelVisualStep> TickVisualSequences(double deltaSeconds)
		{
			foreach (var sequence in _visualSequences.Values)
			{
				foreach (var step in sequence.Advance(deltaSeconds))
					yield return new DueDynelVisualStep(this, sequence.Object, step);

				if (sequence.IsFinished)
					_visualSequences.TryRemove(sequence.Object.Handle, out _);
			}
		}

		// The static world objects within `range` of `position` - the interest query for streaming props as a player
		// walks. Same linear scan as the entity queries above, and the same note applies: fine at map scale, a grid is
		// the optimisation when it stops being.
		public IEnumerable<LiveWorldObject> WorldObjectsInRange(Vector3 position, float range)
		{
			foreach (var obj in WorldObjects)
			{
				if (Vector3.Distance(obj.Position, position) <= range)
					yield return obj;
			}
		}

		// The object a client-supplied wire handle refers to. This is the ONLY way back from a handle to a row: the
		// handle is this zone's, so nothing else can resolve it (it used to be a per-interaction DB query against the
		// CapturedHandle column - see LiveWorldObject).
		public bool TryGetWorldObject(uint handle, [MaybeNullWhen(false)] out LiveWorldObject obj)
		{
			obj = null;
			foreach (var candidate in WorldObjects)
			{
				if (candidate.Handle != handle)
					continue;

				obj = candidate;
				return true;
			}

			return false;
		}

		// Forgets a world object (the GM .delete of a prop) so the zone stops streaming it without a reload.
		public void RemoveWorldObject(uint handle)
		{
			WorldObjects = [.. WorldObjects.Where(o => o.Handle != handle)];
		}

		// Mints the next free wire handle for something this zone streams - an NPC or a static object. ONE counter for
		// both: they share the client's handle space, so separate sequences could hand the same handle to a prop and a
		// creature and the second spawn would replace the first. Stays well below 0x02000000, where the client's own
		// instance handles live (a player's handle IS its character id, e.g. 0x0802E5D4).
		public uint AllocateHandle()
		{
			return Interlocked.Increment(ref _nextHandle);
		}

		public void Add(WorldEntity entity)
		{
			_entities[entity.Handle] = entity;
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

		public bool Remove(uint handle)
		{
			return _entities.TryRemove(handle, out _);
		}

		public bool TryGet(uint handle, [MaybeNullWhen(false)] out WorldEntity entity)
		{
			return _entities.TryGetValue(handle, out entity);
		}
	}
}
