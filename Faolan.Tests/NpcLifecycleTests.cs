using System.Linq;
using System.Numerics;
using Faolan.GameServer.Combat;
using Faolan.GameServer.World;
using Xunit;

namespace Faolan.Tests
{
	// NPC death -> respawn lifecycle in the world model: combat applies damage
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

		// The retail corpse sequence (WorldEntity.CorpseFadeSeconds): the loot bag drops on the kill, and the body is
		// torn down half a minute later. The ordering is the point: holding the bag for 3s and despawning the corpse to
		// make room for it is backwards.
		[Fact]
		public void TickCorpse_DropsLootAtOnceThenFadesTheBodyMuchLater()
		{
			var npc = DeadNpc(600.0); // long respawn so only the corpse sequence is in play

			Assert.Equal(CorpseStage.Drop, npc.TickCorpse(0.1)); // the first tick after the kill drops the bag
			Assert.Null(npc.TickCorpse(0.1)); // ...exactly once
			Assert.Null(npc.TickCorpse(WorldEntity.CorpseFadeSeconds - 1.0)); // the body is still lying there at ~30s

			Assert.Equal(CorpseStage.Fade, npc.TickCorpse(1.0)); // 30.8s -> fade the body out
			Assert.Null(npc.TickCorpse(1.0)); // the quit waits out its own 2.5s gap, it does not follow immediately
			Assert.Equal(CorpseStage.Quit, npc.TickCorpse(WorldEntity.CorpseQuitDelaySeconds)); // 33.3s -> drop the handle

			Assert.Null(npc.TickCorpse(100.0)); // and the sequence is spent for this death
			Assert.True(npc.CorpseCleared);
		}

		// A long stalled tick may cross two stages at once; it must still put them on the wire one at a time, or a fade
		// and the quit that ends it go out in the same breath with no dissolve in between.
		[Fact]
		public void TickCorpse_EmitsOneStagePerTickEvenWhenDtOverruns()
		{
			var npc = DeadNpc(600.0);
			Assert.Equal(CorpseStage.Drop, npc.TickCorpse(1000.0));
			Assert.Equal(CorpseStage.Fade, npc.TickCorpse(0.1));
			Assert.Equal(CorpseStage.Quit, npc.TickCorpse(0.1));
		}

		// A revive part-way through abandons the sequence, and the next death starts it over from the top.
		[Fact]
		public void TickCorpse_RestartsFromTheTopAfterARespawn()
		{
			var npc = DeadNpc(1.0);
			Assert.Equal(CorpseStage.Drop, npc.TickCorpse(0.1));
			Assert.True(npc.TickRespawn(1.0));
			Assert.False(npc.CorpseCleared);
			Assert.Null(npc.TickCorpse(100.0)); // alive - no corpse to run

			npc.Vitals.ApplyHealthDelta(-80);
			npc.MarkDead();
			Assert.Equal(CorpseStage.Drop, npc.TickCorpse(0.1)); // the new death drops its own bag
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
		public void WorldStateTick_DropsLootOnDeathFadesTheCorpseThenRespawns()
		{
			var world = new WorldState();
			var zone = world.ZoneFor(2040);
			var npc = DeadNpc(60.0);
			zone.Add(npc);

			// The kill itself: the bag drops immediately, and the body stays put.
			var dropped = world.Tick(0.1);
			Assert.Single(dropped);
			Assert.Same(npc, dropped[0].Entity);
			Assert.Equal(WorldTickEffectType.NpcCorpseLootDropped, dropped[0].Type);
			Assert.True(npc.IsDead);

			// ~30.8s: the body fades, then its handle is quit 2.5s later. The bag is not involved in either.
			Assert.Empty(world.Tick(WorldEntity.CorpseFadeSeconds - 1.0));
			Assert.Equal(WorldTickEffectType.NpcCorpseFaded, Assert.Single(world.Tick(1.0)).Type);
			Assert.Equal(WorldTickEffectType.NpcCorpseQuit, Assert.Single(world.Tick(WorldEntity.CorpseQuitDelaySeconds)).Type);
			Assert.True(npc.IsDead); // a placed NPC is still in the zone, waiting out its respawn

			// Respawn timer (60s): revive at the spawn point, long after the corpse has gone.
			Assert.Empty(world.Tick(60.0 - WorldEntity.CorpseFadeSeconds - WorldEntity.CorpseQuitDelaySeconds - 0.2));
			var revived = world.Tick(0.2);
			Assert.Equal(WorldTickEffectType.NpcRespawned, Assert.Single(revived).Type);
			Assert.False(npc.IsDead);
		}

		// A NoRespawn creature (a summon, an ambient pool member) leaves the world when its corpse sequence ends rather
		// than sitting out a respawn timer it will never use, which would park it in the zone for minutes after the
		// client had already stopped rendering it.
		[Fact]
		public void WorldStateTick_NoRespawnCorpseLeavesTheWorldWhenTheSequenceEnds()
		{
			var world = new WorldState();
			var zone = world.ZoneFor(2040);
			var npc = DeadNpc();
			npc.Route.NoRespawn = true;
			zone.Add(npc);

			Assert.Equal(WorldTickEffectType.NpcCorpseLootDropped, Assert.Single(world.Tick(0.1)).Type);
			Assert.Equal(WorldTickEffectType.NpcCorpseFaded, Assert.Single(world.Tick(WorldEntity.CorpseFadeSeconds)).Type);

			var end = world.Tick(WorldEntity.CorpseQuitDelaySeconds);
			Assert.Equal([WorldTickEffectType.NpcCorpseQuit, WorldTickEffectType.NpcCorpseRemoved], end.Select(e => e.Type));
			Assert.All(end, e => Assert.Same(npc, e.Entity));
		}

		[Fact]
		public void WorldStateTick_IgnoresPlayersAndLivingNpcs()
		{
			var world = new WorldState();
			var zone = world.ZoneFor(2040);
			zone.Add(WorldEntity.ForNpc(1, 2040, Vector3.Zero, Quaternion.Identity, "Alive", EntityVitals.FromMax(50, 0, 50)));
			// Player far out of aggro range, so the living NPC neither respawns (alive) nor attacks (out of range).
			zone.Add(new WorldEntity(2, 2040, true, "Player", null!)
			{
				Position = new Vector3(1000, 0, 1000)
			});

			// Neither a living NPC nor a player produces a lifecycle effect. Nothing to filter: the living NPC here
			// carries no ambient animations, so the tick is genuinely silent for it.
			Assert.Empty(world.Tick(100.0));
		}

		[Fact]
		public void WorldStateTick_RespawnsDeadNpcsAndReturnsThem()
		{
			var world = new WorldState();
			var zone = world.ZoneFor(2040);
			var npc = DeadNpc(2.0);
			zone.Add(npc);

			// The first tick drops the corpse's loot bag; the respawn is the only thing after it at this delay (2s is
			// far short of the corpse fade, so the body is still standing when the creature revives under it).
			Assert.Equal(WorldTickEffectType.NpcCorpseLootDropped, Assert.Single(world.Tick(1.0)).Type);
			var effects = world.Tick(1.0); // 2s -> respawns
			Assert.Single(effects);
			Assert.Same(npc, effects[0].Entity);
			Assert.Equal(WorldTickEffectType.NpcRespawned, effects[0].Type);
			Assert.False(npc.IsDead);
		}

		private static WorldEntity DeadNpc(double respawnDelay = 10.0)
		{
			var npc = WorldEntity.ForNpc(0x1234, 2040, new Vector3(10, 0, 20), Quaternion.Identity, "Mob", EntityVitals.FromMax(80, 0, 80));
			npc.Route.RespawnDelaySeconds = respawnDelay;
			npc.Vitals.ApplyHealthDelta(-80); // killing blow
			Assert.True(npc.IsDead);
			npc.MarkDead();
			return npc;
		}
	}
}
