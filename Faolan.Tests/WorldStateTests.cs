using System.Linq;
using System.Numerics;
using Faolan.GameServer.World;
using Xunit;

namespace Faolan.Tests
{
	// The live in-memory world model (docs/server-architecture-roadmap.md): per-map zones, an entity registry, and
	// the spatial range queries interest management uses to send moves/spawns to nearby players only.
	public class WorldStateTests
	{
		private static WorldEntity Player(uint handle, float x, float z) =>
			new WorldEntity(handle, 0, true, $"P{handle}", null) { Position = new Vector3(x, 0, z) };

		private static WorldEntity Npc(uint handle, float x, float z) =>
			new WorldEntity(handle, 0, false, $"N{handle}", null) { Position = new Vector3(x, 0, z) };

		[Fact]
		public void Place_AddsToMapZone_AndRelocatesOnMapChange()
		{
			var world = new WorldState();
			var e = Player(1, 0, 0);

			world.Place(e, 2040, new Vector3(10, 0, 10), Vector3.Zero);
			Assert.Equal(2040u, e.MapId);
			Assert.Equal(1, world.ZoneFor(2040).Count);

			// A map change moves the entity out of the old zone into the new one.
			world.Place(e, 3000, new Vector3(5, 0, 5), Vector3.Zero);
			Assert.Equal(0, world.ZoneFor(2040).Count);
			Assert.Equal(1, world.ZoneFor(3000).Count);
			Assert.True(world.ZoneFor(3000).TryGet(1, out _));
		}

		[Fact]
		public void Remove_TakesEntityOutOfItsZone()
		{
			var world = new WorldState();
			var e = Player(1, 0, 0);
			world.Place(e, 2040, Vector3.Zero, Vector3.Zero);

			world.Remove(e);

			Assert.Equal(0, world.ZoneFor(2040).Count);
		}

		[Fact]
		public void PlayersInRange_ReturnsNearbyPlayers_ExcludesSelfFarAndNpcs()
		{
			var zone = new Zone(2040);
			var self = Player(1, 0, 0);
			var near = Player(2, 30, 0); // within 50
			var far = Player(3, 200, 0); // outside 50
			var npc = Npc(4, 5, 0); // close but not a player
			foreach (var e in new[] { self, near, far, npc })
				zone.Add(e);

			var seen = zone.PlayersInRange(self.Position, 50f, excludeHandle: self.Handle)
				.Select(e => e.Handle)
				.OrderBy(h => h)
				.ToArray();

			Assert.Equal(new uint[] { 2 }, seen); // only the near player; self excluded, far out of range, npc filtered
		}

		[Fact]
		public void EntitiesInRange_IncludesNpcs_WithinRange()
		{
			var zone = new Zone(2040);
			zone.Add(Player(1, 0, 0));
			zone.Add(Npc(4, 5, 0));
			zone.Add(Npc(5, 100, 0));

			var seen = zone.EntitiesInRange(new Vector3(0, 0, 0), 50f)
				.Select(e => e.Handle)
				.OrderBy(h => h)
				.ToArray();

			Assert.Equal(new uint[] { 1, 4 }, seen); // self + near npc; the far npc is out of range
		}
	}
}
