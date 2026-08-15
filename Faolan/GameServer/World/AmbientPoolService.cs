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
	// #363 live ambient spawner: the capture-INDEPENDENT open-world
	// population, managed as spawn-group POOLS. Each RESOLVED NpcSpawnArea becomes an AmbientPool that keeps ~MaxCount
	// of its creature alive within the group's area: the initial fill spawns one per RDB point, each member roams the
	// group's points (its PatrolWaypoints -> the #266 wander) and fades on death (NoRespawn); Replenish
	// then respawns the deficit at a free point. Built WHOLE from the creature TYPE (TryBuildFromType, the pet path).
	// Worked example - Tortage beach (4010): the four T_Crocodile-* areas become four Crocodile pools (8/7/8/11)
	// that each hold that many crocs roaming their stretch of shore, refilled as the player kills them.
	//
	// This owns the pool STATE and the spawning; it sends nothing. `Replenish` hands each newly spawned member back to
	// the caller, which streams it to the players in range - that is the state-vs-wire seam #826 asked for, and it is
	// why this service holds no reference to WorldBroadcaster (a back-reference would re-close the cycle the
	// extraction opened).
	//
	// ⚠️ `Replenish` is a LAZY iterator on purpose. The caller awaits a send between members, and yielding keeps the
	// original spawn-then-send-then-spawn interleaving; materialising the sequence first would spawn every deficit
	// across every pool before the first packet went out.
	public sealed class AmbientPoolService
	{
		// #363 ambient spawn-group pools per loaded zone (built in LoadAsync on the entry thread, iterated by
		// Replenish on the tick thread - concurrent so the tick doesn't throw mid-load).
		private readonly ConcurrentDictionary<(uint MapId, uint InstanceId), List<AmbientPool>> _pools = new ConcurrentDictionary<(uint MapId, uint InstanceId), List<AmbientPool>>();

		private readonly INpcRepository _database;
		private readonly RuntimeHandleAllocator _handles;
		private readonly ILogger _logger;
		private readonly WorldSimulation _sim;

		public AmbientPoolService(WorldSimulation sim, INpcRepository database, RuntimeHandleAllocator handles, ILogger logger)
		{
			_sim = sim;
			_database = database;
			_handles = handles;
			_logger = logger;
		}

		// Build this zone's pools from its resolved spawn areas and fill each to MaxCount. Nothing is streamed: this
		// runs on first entry to the zone, before anyone is watching it.
		public async Task LoadAsync(uint mapId, uint instanceId, Zone zone)
		{
			var areas = await _database.GetResolvedSpawnAreas(mapId);
			var pools = new List<AmbientPool>();
			var spawned = 0;
			foreach (var area in areas)
			{
				var type = area.NpcTemplate;
				if (type?.StatCore?.IirHeader == null)
					continue; // the resolved creature has no buildable stat core - skip (can't construct its body)

				var points = area.Points.Select(p => p.Position).ToArray();
				if (points.Length == 0)
					continue;

				var level = type.StatCore.Level ?? 0;
				var pool = new AmbientPool(area.Id, area.MaxCount, points, type, level, level > 0
					? CombatResolver.NpcMaxHealthForLevel(level)
					: 1u, type.StatCore.Disposition ?? WorldEntity.DispositionFriendly, SpawnAreaLabels.FormatAreaLabel(area));
				pools.Add(pool);

				// The captured spawns LINKED to this area (tagged in the load loop above) are already in the zone as pool
				// members - the "obsolete npcspawns" that fill the pool first; only spawn the DEFICIT up to MaxCount, at
				// points not already occupied. So a resolved area with captured members doesn't double-populate.
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
				_logger.LogInformation("World: +{Count} ambient NPC(s) across {Pools} spawn-area pool(s) on map {Map}.", spawned, pools.Count, mapId);
		}

		// Top every under-strength pool back up, yielding each member as it is spawned so the caller can stream it.
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
			// Bake THIS zone's live token (4-byte) so the body is wire-correct and SendWorldSpawn can re-stamp it per
			// recipient. Passing 0 shipped a 1-byte token that the client read as a version-byte desync (crash on 4010's
			// ambient pools) and that SpawnTransformPatch could not fix (it only overwrites a 4-byte token in place).
			var body = NpcSpawnBuilder.BuildFromType(pool.Type, handle, pos, Quaternion.Identity, zone.PlayfieldToken);

			var entity = WorldEntity.ForNpc(handle, zone.MapId, pos, Quaternion.Identity, pool.Type.Name, EntityVitals.FromMax(pool.MaxHealth, 0, pool.MaxHealth), pool.Type.TemplateId);
			entity.InstanceId = zone.InstanceId;
			entity.Level = pool.Level;
			entity.Disposition = pool.Disposition;
			entity.Hostile = pool.Disposition == WorldEntity.DispositionHostile;
			entity.LootContainer = pool.Type.LootContainerTemplateId; // drop this type's container on death
			entity.SpawnBody = body;
			entity.SpawnAreaId = pool.AreaId;
			entity.OverheadNameOverride = pool.Label; // #363 dev nameplate label, sent via SetNameIIR when .togglenpcgroup is on
			entity.NoRespawn = true; // corpse fades on death; the POOL respawns a replacement (below)
			entity.PatrolWaypoints = pool.Points; // roam the group's area via the #266 wander (no new movement code)
			entity.ArmPatrolCycle(); // stagger the first wander so they don't all step at once
			foreach (var s in pool.Type.Spells)
				entity.Spells.Add(new NpcSpellCast(s.SpellId, NpcSpellEffectColumns.ToBuffData(s.SpellId, s.Duration, s.DescriptorSlot3, s.CastingLevel, s.FlagA, s.DescriptorSlot0, s.DescriptorSlot8)));

			zone.Add(entity);
			return entity;
		}

		// A pool point with no live member sitting on it (so a refill lands on an empty spot, not on top of a survivor);
		// falls back to a random point when they're all occupied.
		private static Vector3 FreePoint(AmbientPool pool, IReadOnlyList<WorldEntity> live)
		{
			foreach (var pt in pool.Points)
			{
				var occupied = false;
				foreach (var e in live)
				{
					var dx = e.Position.X - pt.X;
					var dz = e.Position.Z - pt.Z;
					if (dx * dx + dz * dz < 4f)
					{
						occupied = true;
						break;
					}
				}

				if (!occupied)
					return pt;
			}

			return pool.Points[Random.Shared.Next(pool.Points.Length)];
		}

		// The runtime state of one resolved NpcSpawnArea: the creature TYPE + spawn points + the population it maintains.
		// Label is the dev nameplate tag (SpawnAreaLabels.FormatAreaLabel), applied to members when the GM's
		// `.togglenpcgroup` debug labelling is on.
		private sealed record AmbientPool(uint AreaId, int MaxCount, Vector3[] Points, NpcTemplate Type, uint Level, uint MaxHealth, uint Disposition, string Label);
	}
}
