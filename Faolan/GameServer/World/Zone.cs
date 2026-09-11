using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using System.Threading;

namespace Faolan.GameServer.World
{
	/// <summary>
	///     One map's live entity registry: every player and NPC currently in that map, keyed by wire handle.
	/// </summary>
	/// <remarks>
	///     Answers the spatial queries interest management needs - who is near a point - so a movement or spawn reaches
	///     only nearby players. Range scans are linear, which is correct and cheap at the entity counts a beach-scale
	///     map carries; a uniform grid is the optimisation once they grow.
	/// </remarks>
	public sealed class Zone
	{
		private readonly ConcurrentDictionary<uint, WorldEntity> _entities = new ConcurrentDictionary<uint, WorldEntity>();

		/// <summary>
		///     Which copy (instance) of <see cref="MapId" /> this zone is. Together with <see cref="MapId" /> it is the
		///     zone's identity key in <see cref="WorldState" />.
		/// </summary>
		/// <remarks>
		///     Instance 0 is the default shared copy; a Solo or Group map mints a non-zero id per copy. Distinct from
		///     <see cref="PlayfieldToken" />: the instance id is the stable routing key, while the playfield token is
		///     the value the client learned from the entry burst. Solo copies share one burst token, which is harmless
		///     because a client only ever sees its own instance.
		/// </remarks>
		public uint InstanceId { get; set; }

		/// <summary>Whether this copy is the map's epic difficulty, stamped at entry from the player's travel-box pick.</summary>
		/// <remarks>
		///     It changes routing - an epic copy never shares with a normal one - and the MapChange's class word
		///     (0xCABC rather than 0xC79C). The content is deliberately the normal map's until the epic tuning is
		///     modelled; measured so far, roughly 8x health on non-minion mobs - see <c>NpcSpawnExtractor</c>'s
		///     epic-window note. Open work tracked in Gitea #309.
		/// </remarks>
		public bool IsEpic { get; set; }

		private uint _nextHandle;

		public uint MapId { get; }

		public IReadOnlyCollection<WorldEntity> Entities => _entities.Values.ToArray();

		public int Count => _entities.Count;

		/// <summary>
		///     The per-playfield-instance token stamped into every despawn so the client scopes the removal to this
		///     playfield.
		/// </summary>
		/// <remarks>
		///     <see cref="WorldState" /> mints one on zone creation as a fallback; for a replayed playfield the burst's
		///     own token - the value woven into the 0x737A6DF9 snapshot tail, which is the one the client actually
		///     learned - overrides it via <c>PlayfieldTokenScan.TryExtract</c>.
		/// </remarks>
		public uint PlayfieldToken { get; set; }

		/// <summary>
		///     The map's static world objects: props, doors, climbing vines and destructibles, loaded once per zone by
		///     <c>ZonePopulationLoader.EnsureZoneWorldObjects</c> rather than re-queried per world entry.
		/// </summary>
		/// <remarks>
		///     They are deliberately not <see cref="WorldEntity" /> instances: they do not move, think, fight or tick,
		///     so putting ~1000 inert props in the entity map would buy nothing and cost the simulation a pass over all
		///     of them. What they do need is interest streaming, which is why they live on the zone - see
		///     <see cref="WorldObjectsInRange" />. A loot bag is the exception and is a <see cref="WorldEntity" />,
		///     because it has a lifecycle (open, expire, fade), which is the state that earns entity-hood.
		/// </remarks>
		public IReadOnlyList<LiveWorldObject> WorldObjects { get; set; } = [];

		// The staged visual sequences currently running on this zone's objects - a harvested node fading out, swapping
		// its mesh, and growing back. Keyed by handle so a second harvest of the same node replaces its sequence
		// instead of interleaving two of them.
		private readonly ConcurrentDictionary<uint, DynelVisualSequence> _visualSequences = new ConcurrentDictionary<uint, DynelVisualSequence>();

		public Zone(uint mapId)
		{
			MapId = mapId;
		}

		/// <summary>
		///     Starts (or restarts) an object's harvest visual sequence. The caller has already sent its opening
		///     fade-out; this schedules everything that follows.
		/// </summary>
		public void StartVisualSequence(LiveWorldObject obj)
		{
			_visualSequences[obj.Handle] = new DynelVisualSequence(obj);
		}

		/// <summary>
		///     Advances every running sequence by <paramref name="deltaSeconds" /> and yields the steps that came due.
		/// </summary>
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

		/// <summary>
		///     How long a harvest node keeps its "being gathered" map pin after the last swing on it.
		/// </summary>
		/// <remarks>
		///     Retail restores the node's own kind 10-15 s after the last health drop of a bout (10 s on 7 of 16 bouts,
		///     never sooner), counted from the last swing rather than the first, so a bout of four swings holds the pin
		///     for roughly 40 s.
		/// </remarks>
		public const double GatheringPinSeconds = 10.0;

		// The harvest nodes currently showing the gathering pin, each with the seconds left before its own kind comes
		// back. Keyed by handle, like the visual sequences, so every swing resets the countdown rather than queueing a
		// second one.
		private readonly ConcurrentDictionary<uint, (LiveWorldObject Node, double SecondsLeft)> _gatheringPins = new ConcurrentDictionary<uint, (LiveWorldObject, double)>();

		/// <summary>A swing landed on the node: start, or extend, its gathering pin.</summary>
		/// <returns>
		///     Whether the node was not already showing the pin - the one case it has to go on the wire. A further
		///     swing inside the window only restarts the countdown and sends nothing, which is what retail does: 23 of
		///     23 un-flagged swings in the corpus landed on a node already at <c>ResourceGatheringActive</c>.
		/// </returns>
		public bool MarkGathering(LiveWorldObject node)
		{
			var isNew = !_gatheringPins.ContainsKey(node.Handle);
			_gatheringPins[node.Handle] = (node, GatheringPinSeconds);
			return isNew;
		}

		/// <summary>
		///     Counts every gathering pin down by <paramref name="deltaSeconds" /> and yields the nodes whose window
		///     ran out, removing them.
		/// </summary>
		public IEnumerable<LiveWorldObject> TickGatheringPins(double deltaSeconds)
		{
			foreach (var (handle, (node, left)) in _gatheringPins)
			{
				var remaining = left - deltaSeconds;
				if (remaining > 0)
				{
					_gatheringPins[handle] = (node, remaining);
					continue;
				}

				if (_gatheringPins.TryRemove(handle, out _))
					yield return node;
			}
		}

		/// <summary>
		///     The static world objects within <paramref name="range" /> of <paramref name="position" /> - the interest
		///     query for streaming props as a player walks.
		/// </summary>
		/// <remarks>A linear scan, like the entity queries: adequate at map scale, with a grid as the optimisation.</remarks>
		public IEnumerable<LiveWorldObject> WorldObjectsInRange(Vector3 position, float range)
		{
			foreach (var obj in WorldObjects)
			{
				if (Vector3.Distance(obj.Position, position) <= range)
					yield return obj;
			}
		}

		/// <summary>The object a client-supplied wire handle refers to.</summary>
		/// <remarks>
		///     The only way back from a handle to a row: the handle is minted by this zone, so nothing else can
		///     resolve it.
		/// </remarks>
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

		/// <summary>
		///     Forgets a world object - the GM delete of a prop - so the zone stops streaming it without a reload.
		/// </summary>
		public void RemoveWorldObject(uint handle)
		{
			WorldObjects = [.. WorldObjects.Where(o => o.Handle != handle)];
		}

		/// <summary>Mints the next free wire handle for something this zone streams - an NPC or a static object.</summary>
		/// <remarks>
		///     One counter serves both: they share the client's handle space, so separate sequences could hand the same
		///     handle to a prop and a creature, and the second spawn would replace the first. The counter stays well
		///     below 0x02000000, where the client's own instance handles live - a player's handle is its character id,
		///     for example 0x0802E5D4.
		/// </remarks>
		public uint AllocateHandle()
		{
			return Interlocked.Increment(ref _nextHandle);
		}

		public void Add(WorldEntity entity)
		{
			_entities[entity.Handle] = entity;
		}

		/// <summary>
		///     Entities within <paramref name="range" /> of <paramref name="position" /> in three dimensions, excluding
		///     <paramref name="excludeHandle" /> - typically the source itself.
		/// </summary>
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

		/// <summary>
		///     Players within <paramref name="range" /> of <paramref name="position" /> - the broadcast recipients for
		///     a move, spawn or chat line.
		/// </summary>
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
