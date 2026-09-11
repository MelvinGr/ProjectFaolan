using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Database.Interfaces;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.GameServer.Combat;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.World
{
	/// <summary>
	///     The live ambient spawner: the capture-independent open-world population, managed as spawn-group pools.
	/// </summary>
	/// <remarks>
	///     Each resolved <c>NpcSpawnArea</c> becomes a pool that keeps about <c>MaxCount</c> of its creature alive
	///     within the group's area. The initial fill spawns one per RDB point, each member roams the group's points as
	///     its patrol waypoints and fades on death, and <see cref="Replenish" /> respawns the deficit at a free point.
	///     Members are built whole from the creature type. On the Tortage beach (4010), for example, the four
	///     T_Crocodile areas become four crocodile pools of 8, 7, 8 and 11, each holding that many crocodiles roaming
	///     its stretch of shore and refilled as the player kills them.
	///     <para>
	///         This service owns the pool state and the spawning and sends nothing: <see cref="Replenish" /> hands each
	///         newly spawned member back to the caller, which streams it to the players in range. That is why it holds
	///         no reference to <see cref="WorldBroadcaster" /> - a back-reference would close a construction cycle.
	///     </para>
	///     <para>
	///         <see cref="Replenish" /> is a lazy iterator by design: the caller awaits a send between members, and
	///         yielding keeps the spawn-send-spawn interleaving. Materialising the sequence first would spawn every
	///         deficit across every pool before the first packet went out.
	///     </para>
	/// </remarks>
	public sealed class AmbientPoolService
	{
		// The ambient spawn-group pools of each loaded zone. Built in LoadAsync on the entry thread and iterated by
		// Replenish on the tick thread, so the map is concurrent: the tick must not throw mid-load.
		private readonly ConcurrentDictionary<(uint MapId, uint InstanceId), List<AmbientPool>> _pools = new ConcurrentDictionary<(uint MapId, uint InstanceId), List<AmbientPool>>();

		private readonly INpcRepository _database;
		private readonly RuntimeHandleAllocator _handles;
		private readonly ILogger _logger;
		private readonly WorldSimulation _sim;

		// Which combos a member swings, derived from the outfit it wears. Resolved at pool load rather than per member,
		// because the spawn path cannot await: Replenish is an iterator and SpawnMember is called from inside it.
		private readonly ComboService _combos;

		public AmbientPoolService(WorldSimulation sim, INpcRepository database, RuntimeHandleAllocator handles, ComboService combos, ILogger logger)
		{
			_sim = sim;
			_database = database;
			_handles = handles;
			_combos = combos;
			_logger = logger;
		}

		/// <summary>
		///     Build this zone's pools from its resolved spawn areas and fill each to its <c>MaxCount</c>. Nothing is
		///     streamed: this runs on first entry to the zone, before anyone is watching it.
		/// </summary>
		public async Task LoadAsync(uint mapId, uint instanceId, Zone zone)
		{
			var clock = System.Diagnostics.Stopwatch.StartNew();
			var areas = await _database.GetResolvedSpawnAreas(mapId);

			// The harvested outfits of each creature this map pools. Retail dresses the individuals of a type
			// differently, so a pool draws from the set rather than putting every member in the type's default.
			var variants = await _database.GetAppearanceVariants(areas.Where(a => a.NpcTemplateId != null).Select(a => a.NpcTemplateId!.Value).Distinct().ToList());
			var pools = new List<AmbientPool>();
			var spawned = 0;
			foreach (var area in areas)
			{
				var type = area.NpcTemplate;
				if (type?.StatCore?.IirHeader == null)
					continue; // the resolved creature has no buildable stat core, so its body cannot be constructed

				var points = area.Points.Select(p => p.Position).ToArray();
				if (points.Length == 0)
					continue;

				var level = type.StatCore.Level ?? 0;
				var outfits = variants.GetValueOrDefault(type.TemplateId, []);

				// One combo set per outfit, resolved here because this is the only async context on the path, and per
				// outfit rather than per member for the same reason the outfits are: a pool of eight draws from a
				// handful of harvested kits, so this is a handful of lookups per pool rather than one per mob. A pool
				// with no harvested outfit gets one entry built from nothing, which is what its members wear.
				var comboSets = new ComboSet[Math.Max(1, outfits.Length)];
				for (var o = 0; o < comboSets.Length; o++)
				{
					var kit = outfits.Length > 0 ? outfits[o].Equipment : null;
					comboSets[o] = new ComboSet(await _combos.NpcMeleeComboId(kit), await _combos.NpcClassComboIds(kit, level, type.StatCore?.ComboClass));
				}

				var pool = new AmbientPool(area.Id, area.MaxCount, points, type, outfits, comboSets, level, level > 0
					? CombatResolver.NpcMaxHealthForLevel(level)
					: 1u, type.StatCore?.Disposition ?? WorldEntity.DispositionFriendly, SpawnAreaLabels.FormatAreaLabel(area));
				pools.Add(pool);

				// The captured spawns linked to this area are already in the zone as pool members, so only the deficit
				// up to MaxCount is spawned, at points not already occupied. A resolved area with captured members
				// therefore does not double-populate.
				var members = zone.Entities.Where(e => e.SpawnAreaId == area.Id).ToList();
				for (var i = members.Count; i < pool.MaxCount; i++)
				{
					var member = SpawnMember(pool, FreePoint(pool, members), zone);
					if (member == null)
						break;

					members.Add(member);
					spawned++;
				}
			}

			_pools[(mapId, instanceId)] = pools;
			if (spawned > 0)
				_logger.LogInformation("World: +{Count} ambient NPC(s) across {Pools} spawn-area pool(s) on map {Map} in {Ms} ms.", spawned, pools.Count, mapId, clock.ElapsedMilliseconds);
		}

		/// <summary>
		///     Top every under-strength pool back up, yielding each member as it is spawned so the caller can stream it.
		/// </summary>
		public IEnumerable<(WorldEntity Entity, Zone Zone, Vector3 Position)> Replenish()
		{
			foreach (var ((mapId, instanceId), pools) in _pools)
			{
				var zone = _sim.World.ZoneFor(mapId, instanceId);
				foreach (var pool in pools)
				{
					var live = zone.Entities.Where(e => e.SpawnAreaId == pool.AreaId && !e.IsDead).ToList();
					if (live.Count >= pool.MaxCount)
						continue;

					var pos = FreePoint(pool, live);
					var entity = SpawnMember(pool, pos, zone);
					if (entity == null)
						continue;

					yield return (entity, zone, pos);
				}
			}
		}

		private WorldEntity SpawnMember(AmbientPool pool, Vector3 pos, Zone zone)
		{
			var handle = _handles.Next();
			// Bake this zone's live 4-byte token into the body so it is wire-correct and SendWorldSpawn can re-stamp it
			// per recipient. A zero ships a 1-byte token the client reads as a version-byte desync, and
			// SpawnTransformPatch cannot repair it, because it only overwrites a 4-byte token in place.
			//
			// The harvested outfits are assigned round-robin by how many of this pool are already standing, so eight
			// poachers are eight individuals and the assignment is stable rather than random: a respawn re-draws in
			// the same order. One index serves both the outfit and its combos, computed once - deriving them from two
			// separate counts would let a mob swing a combo its weapon is not holding the moment the two drift apart.
			var slot = pool.Outfits.Length > 0
				? zone.Entities.Count(e => e.SpawnAreaId == pool.AreaId) % pool.Outfits.Length
				: 0;
			var wearing = pool.Outfits.Length > 0 ? pool.Outfits[slot] : null;
			// An epic copy's pool members wear the elite decorations, as the placed spawns do.
			var body = NpcSpawnBuilder.BuildFromType(pool.Type, handle, pos, Quaternion.Identity, zone.PlayfieldToken, wearing,
				zone.IsEpic ? EpicDecorations.NormalsetFor(pool.Type.StatCore) : null);

			var entity = WorldEntity.ForNpc(handle, zone.MapId, pos, Quaternion.Identity, pool.Type.Name, EntityVitals.FromMax(pool.MaxHealth, 0, pool.MaxHealth), pool.Type.TemplateId);
			entity.InstanceId = zone.InstanceId;
			entity.Level = pool.Level;
			entity.Disposition = pool.Disposition;
			entity.Hostile = pool.Disposition == WorldEntity.DispositionHostile;
			// The same split as the placed spawns: the colour is the disposition, the aggression is the kit.
			entity.Aggressive = NpcWeapon.Initiates(wearing?.Equipment, wearing is { Morphs.Count: > 0 });
			entity.LootContainer = pool.Type.LootContainerTemplateId; // drop this type's container on death
			// What the member shoots with and how far, both taken from the outfit it was dressed in, which is a real
			// harvested placement's kit.
			entity.RangedAttacker = NpcWeapon.IsRanged(wearing?.Equipment);
			entity.AttackRange = NpcWeapon.AttackRange(wearing?.Equipment, WorldState.NpcMeleeRange);
			// And which combos it swings, from the same outfit index.
			var combos = pool.Combos[Math.Min(slot, pool.Combos.Length - 1)];
			entity.MeleeComboId = combos.MeleeId;
			entity.ClassComboIds = combos.ClassIds;
			entity.SpawnBody = body;
			entity.SpawnAreaId = pool.AreaId;
			entity.OverheadNameOverride = pool.Label; // the dev nameplate label, sent when .togglenpcgroup is on
			entity.Route.NoRespawn = true; // the corpse fades on death and the pool respawns a replacement instead
			entity.Route.PatrolWaypoints = pool.Points; // roam the group's area through the ordinary patrol wander
			entity.Route.ArmPatrolCycle(); // stagger the first wander so the members do not all step at once
			foreach (var s in pool.Type.Spells)
				entity.Spells.Add(new NpcSpellCast(s.SpellId, NpcSpellEffectColumns.ToBuffData(s.SpellId, s.Duration, s.DescriptorSlot3, s.CastingLevel, s.FlagA, s.DescriptorSlot0, s.DescriptorSlot8)));

			zone.Add(entity);
			return entity;
		}

		/// <summary>
		///     A pool point with no live member sitting on it, so a refill lands on an empty spot rather than on top of
		///     a survivor.
		/// </summary>
		private static Vector3 FreePoint(AmbientPool pool, IReadOnlyList<WorldEntity> live)
		{
			var occupied = new Vector3[live.Count];
			for (var i = 0; i < live.Count; i++)
				occupied[i] = live[i].Position;

			return FreePoint(pool.Points, occupied);
		}

		/// <summary>
		///     Where the next pool member goes: the first point with full clearance, else the least crowded one.
		/// </summary>
		/// <remarks>
		///     Split from its <c>AmbientPool</c> caller because the rule is pure geometry - it needs the points and
		///     where the live members stand, and none of the pool's creature data.
		/// </remarks>
		internal static Vector3 FreePoint(IReadOnlyList<Vector3> points, IReadOnlyList<Vector3> live)
		{
			// When nothing has full clearance the least crowded point is taken, never a random one: a random pick drops
			// the spacing rule entirely and can hand back the very point a live member is standing on, which puts two
			// creatures inside each other at byte-identical coordinates.
			//
			// Picking the point whose nearest live member is furthest makes that collision impossible while the pool is
			// under strength, which is the only time anything is spawned: a point with somebody standing exactly on it
			// scores 0 and a point with nobody on it scores more, so an exact-coordinate point can only win if every
			// point is exactly occupied, which needs as many members as points. MaxCount equals the point count on all
			// 3,970 shipped areas, so a pool at that population is not topped up at all.
			//
			// The fallback does not fire because of over-subscription: measured over the shipped data, MaxCount equals
			// the point count exactly on every one of the 3,970 areas. It fires because the client's own points can sit
			// closer together than this spacing radius - 91 areas (2.3%) contain 244 such pairs, and in exactly those
			// 91 the greedy walk runs out of clear points 195 spawns before MaxCount. Retail placed those points that
			// close deliberately, so the right answer is to crowd rather than to refuse: the spacing radius is our
			// heuristic and the point list is the client's data.
			var best = points[0];
			var bestGap = -1f;

			foreach (var pt in points)
			{
				var nearest = float.MaxValue;
				foreach (var e in live)
				{
					var dx = e.X - pt.X;
					var dz = e.Z - pt.Z;
					nearest = MathF.Min(nearest, dx * dx + dz * dz);
				}

				// Fully clear: take it. This is the common path.
				if (nearest >= AmbientSpacingSquared)
					return pt;

				if (nearest > bestGap)
				{
					bestGap = nearest;
					best = pt;
				}
			}

			return best;
		}

		/// <summary>
		///     How much clearance a spawn point wants from a live pool member, squared - two units. This is our
		///     heuristic rather than the client's; see <see cref="FreePoint(IReadOnlyList{Vector3},IReadOnlyList{Vector3})" />
		///     for why it cannot always be honoured.
		/// </summary>
		private const float AmbientSpacingSquared = 4f;

		/// <summary>
		///     The runtime state of one resolved <c>NpcSpawnArea</c>: the creature type, its spawn points, and the
		///     population it maintains.
		/// </summary>
		/// <param name="Outfits">
		///     The distinct harvested appearances of this creature, drawn from in spawn order so a pool is a crowd of
		///     individuals rather than a uniform. Empty for a type with no harvested placement, in which case the type's
		///     own appearance is the fallback - the same one a summoned pet uses.
		/// </param>
		/// <param name="Label">
		///     The dev nameplate tag, applied to members when the GM's <c>.togglenpcgroup</c> labelling is on.
		/// </param>
		private sealed record AmbientPool(uint AreaId, int MaxCount, Vector3[] Points, NpcTemplate Type, NpcSpawn[] Outfits, ComboSet[] Combos, uint Level, uint MaxHealth,
			uint Disposition, string Label);

		/// <summary>
		///     The combos one outfit swings: the generic weapon combo plus the level-gated class family.
		/// </summary>
		private sealed record ComboSet(uint MeleeId, uint[] ClassIds);
	}
}
