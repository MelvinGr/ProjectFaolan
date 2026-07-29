using System.Linq;
using Faolan.Core.Database;
using Faolan.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// #490: a MoveTask can send an NPC's roam route BY REFERENCE instead of by value - `PathFlags & 1` swaps the
	// explicit waypoint list for two u16 that index the playfield's territory table (RDB 1010506,
	// `GameData::PlayfieldTerritory_c`), which the CLIENT already holds. `PathMoveTask::InitRoamPath` resolves
	// `territories[TerritoryIndex].paths[TerritoryPathIndex]` and walks it, bounds-checking both.
	//
	// That claim is only worth anything if the two indices really do address our imported territory table, so these
	// gates join the captured MoveTask nodes against `NpcSpawnAreas` and assert both bounds hold. The client's own
	// check is the model: territory index < territory count, path index < that territory's path count. If a future
	// re-harvest renumbers the areas, or the two u16 are ever re-read in the wrong order, these fail.
	public class TerritoryRoamPathTests
	{
		[Fact]
		public void EveryCapturedTerritoryIndex_AddressesARealTerritoryOnThatMap()
		{
			using var ctx = new DatabaseContext(TestDb.ReadOnly(TestPaths.FaolanDb));

			// (map, ordinal) is the territory's stable identity - the same pair the RDB group carries.
			var territories = ctx
				.NpcSpawnAreas.AsNoTracking()
				.Select(a => new
				{
					a.MapId,
					a.GroupOrdinal
				})
				.ToHashSet();

			var nodes = ctx
				.PatrolNodes.AsNoTracking()
				.Where(p => p.TerritoryIndex != null)
				.Join(ctx.NpcSpawns.AsNoTracking(), p => p.NpcSpawnId, s => s.Id, (p, s) => new
				{
					s.MapId,
					Index = p.TerritoryIndex!.Value
				})
				.ToList();

			Assert.NotEmpty(nodes); // the captures do carry by-reference roam routes; a zero here means they were lost
			var orphans = nodes
				.Where(n => !territories.Contains(new
				{
					n.MapId,
					GroupOrdinal = n.Index
				}))
				.ToList();

			Assert.True(orphans.Count == 0,
				$"{orphans.Count}/{nodes.Count} MoveTask territory indices address no territory on their map: " +
				string.Join(", ", orphans.Take(5).Select(o => $"map {o.MapId} index {o.Index}")));
		}

		[Fact]
		public void EveryCapturedTerritoryPathIndex_IsWithinThatTerritorysPathCount()
		{
			using var ctx = new DatabaseContext(TestDb.ReadOnly(TestPaths.FaolanDb));

			var pathCounts = ctx
				.NpcSpawnAreas.AsNoTracking()
				.ToDictionary(a => (a.MapId, a.GroupOrdinal), a => a.RoamPathCount);

			var nodes = ctx
				.PatrolNodes.AsNoTracking()
				.Where(p => p.TerritoryIndex != null && p.TerritoryPathIndex != null)
				.Join(ctx.NpcSpawns.AsNoTracking(), p => p.NpcSpawnId, s => s.Id, (p, s) => new
				{
					s.MapId,
					Index = p.TerritoryIndex!.Value,
					Path = p.TerritoryPathIndex!.Value
				})
				.ToList();

			Assert.NotEmpty(nodes);
			var bad = nodes
				.Where(n => !pathCounts.TryGetValue((n.MapId, (int)n.Index), out var count) || n.Path >= count)
				.ToList();

			Assert.True(bad.Count == 0,
				$"{bad.Count}/{nodes.Count} MoveTask path indices fall outside their territory's RoamPathCount: " +
				string.Join(", ", bad.Take(5).Select(b => $"map {b.MapId} territory {b.Index} path {b.Path}")));
		}
	}
}
