using System.Linq;
using System.Numerics;
using Faolan.GameServer.Combat;
using Faolan.GameServer.World;
using Xunit;

namespace Faolan.Tests
{
	// The live in-memory world model (docs/archive/server-architecture-roadmap.md): per-map zones, an entity registry, and
	// the spatial range queries interest management uses to send moves/spawns to nearby players only.
	public class WorldStateTests
	{
		[Fact]
		public void EntitiesInRange_IncludesNpcs_WithinRange()
		{
			var zone = new Zone(2040);
			zone.Add(Player(1, 0, 0));
			zone.Add(Npc(4, 5, 0));
			zone.Add(Npc(5, 100, 0));

			var seen = zone
				.EntitiesInRange(new Vector3(0, 0, 0), 50f)
				.Select(e => e.Handle)
				.OrderBy(h => h)
				.ToArray();

			Assert.Equal(new uint[] { 1, 4 }, seen); // self + near npc; the far npc is out of range
		}

		[Fact]
		public void NextPlayfieldToken_IsMonotonic()
		{
			var world = new WorldState();
			Assert.True(world.NextPlayfieldToken() < world.NextPlayfieldToken());
		}

		[Fact]
		public void Place_AddsToMapZone_AndRelocatesOnMapChange()
		{
			var world = new WorldState();
			var e = Player(1, 0, 0);

			world.Place(e, 2040, new Vector3(10, 0, 10), Vector3.Zero);
			Assert.Equal(2040u, e.MapId);
			Assert.Equal(1, world.ZoneFor(2040)
				.Count);

			// A map change moves the entity out of the old zone into the new one.
			world.Place(e, 3000, new Vector3(5, 0, 5), Vector3.Zero);
			Assert.Equal(0, world.ZoneFor(2040)
				.Count);
			Assert.Equal(1, world.ZoneFor(3000)
				.Count);
			Assert.True(world
				.ZoneFor(3000)
				.TryGet(1, out _));
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

			var seen = zone
				.PlayersInRange(self.Position, 50f, self.Handle)
				.Select(e => e.Handle)
				.OrderBy(h => h)
				.ToArray();

			Assert.Equal(new uint[] { 2 }, seen); // only the near player; self excluded, far out of range, npc filtered
		}

		[Fact]
		public void Remove_TakesEntityOutOfItsZone()
		{
			var world = new WorldState();
			var e = Player(1, 0, 0);
			world.Place(e, 2040, Vector3.Zero, Vector3.Zero);

			world.Remove(e);

			Assert.Equal(0, world.ZoneFor(2040)
				.Count);
		}

		[Fact]
		public void ZoneFor_MintsAMonotonicPlayfieldToken()
		{
			var world = new WorldState();

			var first = world.ZoneFor(2040)
				.PlayfieldToken;
			var second = world.ZoneFor(4010)
				.PlayfieldToken;

			Assert.NotEqual(0u, first); // every zone has a token from creation (despawns never skipped for lack of one)
			Assert.True(second > first, "playfield tokens are allocated monotonically");
			// re-fetching the same map returns the same zone, not a fresh token
			Assert.Equal(first, world.ZoneFor(2040)
				.PlayfieldToken);
		}

		[Fact]
		public void Tick_NpcEngagesAndDisengages_EmitsCombatModeEffects()
		{
			// A hostile NPC with a player in aggro range engages: the tick flips InCombat and emits NpcEnterCombat so the
			// broadcaster can draw the NPC's weapon (#218). When the player leaves range it disengages: InCombat clears
			// and NpcExitCombat is emitted (sheathe). The transition fires once, not every tick.
			var world = new WorldState();
			var npc = WorldEntity.ForNpc(10, 2040, new Vector3(0, 0, 0), Vector3.Zero, "Mob", EntityVitals.FromMax(100, 0, 100));
			npc.Hostile = true;
			var player = Player(1, 5, 0); // within aggro range (20)
			world.Place(npc, 2040, new Vector3(0, 0, 0), Vector3.Zero);
			world.Place(player, 2040, player.Position, Vector3.Zero);

			var first = world.Tick(0.1);
			Assert.Contains(first, e => e.Type == WorldTickEffectType.NpcEnterCombat && e.Entity == npc);
			Assert.True(npc.InCombat);

			// Already engaged: a second tick must NOT re-emit enter-combat.
			var second = world.Tick(0.1);
			Assert.DoesNotContain(second, e => e.Type == WorldTickEffectType.NpcEnterCombat);

			// Player walks far away -> NPC disengages and sheathes.
			player.Position = new Vector3(500, 0, 0);
			var third = world.Tick(0.1);
			Assert.Contains(third, e => e.Type == WorldTickEffectType.NpcExitCombat && e.Entity == npc);
			Assert.False(npc.InCombat);
		}

		private static WorldEntity Npc(uint handle, float x, float z)
		{
			return new WorldEntity(handle, 0, false, $"N{handle}", null) { Position = new Vector3(x, 0, z) };
		}

		private static WorldEntity Player(uint handle, float x, float z)
		{
			return new WorldEntity(handle, 0, true, $"P{handle}", null) { Position = new Vector3(x, 0, z) };
		}
	}
}
