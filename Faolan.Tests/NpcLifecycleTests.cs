using System.Linq;
using System.Numerics;
using Faolan.GameServer.Combat;
using Faolan.GameServer.World;
using Xunit;

namespace Faolan.Tests
{
	// NPC death -> respawn lifecycle in the world model (docs/server-architecture-roadmap.md): combat applies damage
	// to the NPC's world-entity vitals; on death the tick counts down a respawn delay and revives it at its spawn point.
	public class NpcLifecycleTests
	{
		private static WorldEntity DeadNpc(double respawnDelay = 10.0)
		{
			var npc = WorldEntity.ForNpc(0x1234, 2040, new Vector3(10, 0, 20), new Vector3(0, 1, 0), "Mob",
				EntityVitals.FromMax(80, 0, 80));
			npc.RespawnDelaySeconds = respawnDelay;
			npc.Vitals.ApplyHealthDelta(-80); // killing blow
			Assert.True(npc.IsDead);
			npc.MarkDead();
			return npc;
		}

		[Fact]
		public void TickRespawn_RevivesAtSpawnPointAfterDelay()
		{
			var npc = DeadNpc(respawnDelay: 3.0);
			// move it away from its spawn point before death (as if it had wandered) to prove respawn snaps it back
			npc.Position = new Vector3(99, 0, 99);

			Assert.False(npc.TickRespawn(1.0)); // 1s elapsed, still dead
			Assert.False(npc.TickRespawn(1.0)); // 2s
			Assert.True(npc.IsDead);

			Assert.True(npc.TickRespawn(1.0)); // 3s -> respawn fires
			Assert.False(npc.IsDead);
			Assert.Equal(npc.Vitals.MaxHealth, npc.Vitals.Health); // full health
			Assert.Equal(new Vector3(10, 0, 20), npc.Position); // back at spawn point
		}

		[Fact]
		public void TickRespawn_NoOpWhileAlive()
		{
			var npc = WorldEntity.ForNpc(1, 2040, Vector3.Zero, Vector3.Zero, "Mob", EntityVitals.FromMax(50, 0, 50));
			Assert.False(npc.TickRespawn(100.0));
			Assert.False(npc.IsDead);
		}

		[Fact]
		public void Entity_WithoutVitals_IsNeverDead()
		{
			var npc = WorldEntity.ForNpc(1, 2040, Vector3.Zero, Vector3.Zero, "Decor");
			Assert.False(npc.IsDead);
			Assert.False(npc.TickRespawn(100.0));
		}

		[Fact]
		public void WorldStateTick_RespawnsDeadNpcsAndReturnsThem()
		{
			var world = new WorldState();
			var zone = world.ZoneFor(2040);
			var npc = DeadNpc(respawnDelay: 2.0);
			zone.Add(npc);

			Assert.Empty(world.Tick(1.0)); // 1s, still dead
			var effects = world.Tick(1.0); // 2s -> respawns
			Assert.Single(effects);
			Assert.Same(npc, effects[0].Entity);
			Assert.Equal(WorldTickEffectType.NpcRespawned, effects[0].Type);
			Assert.False(npc.IsDead);
		}

		[Fact]
		public void WorldStateTick_IgnoresPlayersAndLivingNpcs()
		{
			var world = new WorldState();
			var zone = world.ZoneFor(2040);
			zone.Add(WorldEntity.ForNpc(1, 2040, Vector3.Zero, Vector3.Zero, "Alive", EntityVitals.FromMax(50, 0, 50)));
			// Player far out of aggro range, so the living NPC neither respawns (alive) nor attacks (out of range).
			zone.Add(new WorldEntity(2, 2040, true, "Player", null) { Position = new Vector3(1000, 0, 1000) });

			Assert.Empty(world.Tick(100.0));
		}
	}
}
