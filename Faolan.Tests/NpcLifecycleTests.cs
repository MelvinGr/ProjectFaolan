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
		[Fact]
		public void Entity_WithoutVitals_IsNeverDead()
		{
			var npc = WorldEntity.ForNpc(1, 2040, Vector3.Zero, Quaternion.Identity, "Decor");
			Assert.False(npc.IsDead);
			Assert.False(npc.TickRespawn(100.0));
		}

		[Fact]
		public void TickCorpseLinger_FiresOnceAfterLingerThenStops()
		{
			var npc = DeadNpc(20.0); // long respawn so only the linger is in play
			Assert.False(npc.TickCorpseLinger(1.0)); // 1s, still the dying creature
			Assert.False(npc.TickCorpseLinger(1.0)); // 2s
			Assert.True(npc.TickCorpseLinger(1.0)); // 3s -> corpse becomes lootable (CorpseLingerSeconds)
			Assert.False(npc.TickCorpseLinger(1.0)); // already converted -> never fires again this death
		}

		[Fact]
		public void TickRespawn_NoOpWhileAlive()
		{
			var npc = WorldEntity.ForNpc(1, 2040, Vector3.Zero, Quaternion.Identity, "Mob", EntityVitals.FromMax(50, 0, 50));
			Assert.False(npc.TickRespawn(100.0));
			Assert.False(npc.IsDead);
		}

		[Fact]
		public void TickRespawn_RevivesAtSpawnPointAfterDelay()
		{
			var npc = DeadNpc(3.0);
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
		public void WorldStateTick_CorpseBecomesLootableThenRespawns()
		{
			var world = new WorldState();
			var zone = world.ZoneFor(2040);
			var npc = DeadNpc();
			zone.Add(npc);

			// Death-animation linger (~3s): the corpse converts to a lootable bag, still dead.
			Assert.Empty(world.Tick(2.0)); // 2s
			var lootable = world.Tick(1.0); // 3s
			Assert.Single(lootable);
			Assert.Same(npc, lootable[0].Entity);
			Assert.Equal(WorldTickEffectType.NpcCorpseLootable, lootable[0].Type);
			Assert.True(npc.IsDead);

			// Respawn timer (10s): revive at the spawn point.
			Assert.Empty(world.Tick(6.0)); // 9s
			var revived = world.Tick(1.0); // 10s
			Assert.Single(revived);
			Assert.Equal(WorldTickEffectType.NpcRespawned, revived[0].Type);
			Assert.False(npc.IsDead);
		}

		[Fact]
		public void WorldStateTick_IgnoresPlayersAndLivingNpcs()
		{
			var world = new WorldState();
			var zone = world.ZoneFor(2040);
			zone.Add(WorldEntity.ForNpc(1, 2040, Vector3.Zero, Quaternion.Identity, "Alive", EntityVitals.FromMax(50, 0, 50)));
			// Player far out of aggro range, so the living NPC neither respawns (alive) nor attacks (out of range).
			zone.Add(new WorldEntity(2, 2040, true, "Player", null)
			{
				Position = new Vector3(1000, 0, 1000)
			});

			// The living NPC pulses its idle heartbeat (#225) but produces no respawn/loot effect; a player produces
			// none. Filter the heartbeat out - the invariant here is "no lifecycle effect for a living NPC or a player".
			Assert.DoesNotContain(world.Tick(100.0), e => e.Type != WorldTickEffectType.NpcHeartbeat);
		}

		[Fact]
		public void WorldStateTick_RespawnsDeadNpcsAndReturnsThem()
		{
			var world = new WorldState();
			var zone = world.ZoneFor(2040);
			var npc = DeadNpc(2.0);
			zone.Add(npc);

			Assert.Empty(world.Tick(1.0)); // 1s, still dead
			var effects = world.Tick(1.0); // 2s -> respawns
			Assert.Single(effects);
			Assert.Same(npc, effects[0].Entity);
			Assert.Equal(WorldTickEffectType.NpcRespawned, effects[0].Type);
			Assert.False(npc.IsDead);
		}

		private static WorldEntity DeadNpc(double respawnDelay = 10.0)
		{
			var npc = WorldEntity.ForNpc(0x1234, 2040, new Vector3(10, 0, 20), Quaternion.Identity, "Mob", EntityVitals.FromMax(80, 0, 80));
			npc.RespawnDelaySeconds = respawnDelay;
			npc.Vitals.ApplyHealthDelta(-80); // killing blow
			Assert.True(npc.IsDead);
			npc.MarkDead();
			return npc;
		}
	}
}
