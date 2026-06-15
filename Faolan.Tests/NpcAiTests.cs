using System.Numerics;
using Faolan.GameServer.Combat;
using Faolan.GameServer.World;
using Xunit;

namespace Faolan.Tests
{
	// NPC aggro + attack AI on the tick (docs/archive/server-architecture-roadmap.md): a living NPC with a player in aggro
	// range swings on a fixed cadence, emitting an NpcAttack effect for the listener to turn into a vitals hit.
	public class NpcAiTests
	{
		[Fact]
		public void AttacksNearestPlayer()
		{
			var world = new WorldState();
			var zone = world.ZoneFor(4010);
			var npc = Npc(1, 0, 0);
			var far = Player(2, 15, 0);
			var near = Player(3, 3, 0);
			zone.Add(npc);
			zone.Add(far);
			zone.Add(near);

			var effects = world.Tick(WorldState.NpcAttackInterval);
			var attack = Assert.Single(effects, e => e.Type == WorldTickEffectType.NpcAttack);
			Assert.Same(near, attack.Target);
		}

		[Fact]
		public void DamagedNpc_DoesNotHealWhilePlayerInRange()
		{
			var world = new WorldState();
			var zone = world.ZoneFor(4010);
			var npc = Npc(1, 0, 0);
			npc.Vitals.ApplyHealthDelta(-50);
			zone.Add(npc);
			zone.Add(Player(2, 3, 0)); // still in combat range

			world.Tick(0.1);
			Assert.Equal(50u, npc.Vitals.Health); // still damaged - no leash heal mid-fight
		}

		[Fact]
		public void DamagedNpc_LeashHealsWhenNoPlayerInRange()
		{
			var world = new WorldState();
			var zone = world.ZoneFor(4010);
			var npc = Npc(1, 0, 0);
			npc.Vitals.ApplyHealthDelta(-50); // hurt to 50/100
			zone.Add(npc);
			zone.Add(Player(2, 1000, 1000)); // fled far away

			world.Tick(0.1);
			Assert.Equal(npc.Vitals.MaxHealth, npc.Vitals.Health); // healed back to full
		}

		[Fact]
		public void DeadNpc_DoesNotAttack()
		{
			var world = new WorldState();
			var zone = world.ZoneFor(4010);
			var npc = Npc(1, 0, 0);
			npc.Vitals.ApplyHealthDelta(-100); // dead
			npc.MarkDead();
			zone.Add(npc);
			zone.Add(Player(2, 5, 0));

			// No attack from a corpse (it counts down to respawn instead).
			Assert.DoesNotContain(world.Tick(WorldState.NpcAttackInterval), e => e.Type == WorldTickEffectType.NpcAttack);
		}

		[Fact]
		public void NonHostileNpc_DoesNotAttack()
		{
			var world = new WorldState();
			var zone = world.ZoneFor(4010);
			zone.Add(Npc(1, 0, 0, hostile: false)); // a quest-giver / vendor
			zone.Add(Player(2, 3, 0)); // right next to it

			Assert.Empty(world.Tick(WorldState.NpcAttackInterval));
		}

		[Fact]
		public void NoPlayerInRange_NoAttack()
		{
			var world = new WorldState();
			var zone = world.ZoneFor(4010);
			zone.Add(Npc(1, 0, 0));
			zone.Add(Player(2, 1000, 1000)); // far away

			Assert.Empty(world.Tick(WorldState.NpcAttackInterval * 2));
		}

		[Fact]
		public void PlayerInMeleeRange_AttacksOnCadence()
		{
			var world = new WorldState();
			var zone = world.ZoneFor(4010);
			var npc = Npc(1, 0, 0);
			var player = Player(2, 2, 0); // within NpcMeleeRange (3)
			zone.Add(npc);
			zone.Add(player);

			// First contact swings immediately (cooldown starts at 0), targeting the player (alongside the one-time
			// follow effect for the engagement).
			var first = world.Tick(0.1);
			var attack = Assert.Single(first, e => e.Type == WorldTickEffectType.NpcAttack);
			Assert.Same(npc, attack.Entity);
			Assert.Same(player, attack.Target);

			// Within the interval, no further swing.
			Assert.DoesNotContain(world.Tick(WorldState.NpcAttackInterval / 2), e => e.Type == WorldTickEffectType.NpcAttack);
			// Once the interval elapses, it swings again.
			Assert.Contains(world.Tick(WorldState.NpcAttackInterval), e => e.Type == WorldTickEffectType.NpcAttack);
		}

		[Fact]
		public void PlayerBeyondMeleeRange_ChasesTowardPlayer()
		{
			var world = new WorldState();
			var zone = world.ZoneFor(4010);
			var npc = Npc(1, 0, 0);
			var player = Player(2, 10, 0); // aggro range (20) but beyond melee (3)
			zone.Add(npc);
			zone.Add(player);

			// First chase tick tells the client to follow the player (client-driven movement); the model also steps.
			var follow = Assert.Single(world.Tick(0.5));
			Assert.Equal(WorldTickEffectType.NpcFollow, follow.Type);
			Assert.Same(npc, follow.Entity);
			Assert.Same(player, follow.Target);
			Assert.True(npc.Position.X > 0 && npc.Position.X < 10, "model stepped toward the player");

			// Already following: subsequent chase ticks step the model silently (no repeated follow/move effect).
			var startX = npc.Position.X;
			Assert.Empty(world.Tick(0.5));
			Assert.True(npc.Position.X > startX, "model kept closing the gap");
		}

		[Fact]
		public void LeashedNpc_StopsChasingAndReturnsHome()
		{
			var world = new WorldState();
			var zone = world.ZoneFor(4010);
			var npc = Npc(1, 0, 0); // spawns (and homes) at the origin
			npc.Position = new Vector3(WorldState.NpcLeashRange + 5f, 0, 0); // already chased out past the leash
			zone.Add(npc);
			zone.Add(Player(2, WorldState.NpcLeashRange + 6f, 0)); // a player right next to it, well within aggro range

			// First tick past the leash flips it to returning; it then ignores the adjacent player and heads home.
			world.Tick(0.5);
			Assert.True(npc.Returning, "NPC should leash after straying past NpcLeashRange");

			for (var i = 0; i < 400 && npc.Returning; i++)
				world.Tick(0.5);
			Assert.False(npc.Returning);
			Assert.True(npc.Position.X < WorldState.NpcMeleeRange + 0.001f, "NPC returned home, not parked by the player");
		}

		[Fact]
		public void Chase_ArrivesInMeleeRangeThenAttacks()
		{
			var world = new WorldState();
			var zone = world.ZoneFor(4010);
			var npc = Npc(1, 0, 0);
			zone.Add(npc);
			zone.Add(Player(2, 8, 0));

			// Several seconds of chasing closes the gap; eventually a swing lands once inside melee range.
			var sawAttack = false;
			for (var i = 0; i < 40 && !sawAttack; i++)
				foreach (var e in world.Tick(0.5))
					sawAttack |= e.Type == WorldTickEffectType.NpcAttack;

			Assert.True(sawAttack);
		}

		[Fact]
		public void ResolveNpcAttackDamage_ScalesWithLevel()
		{
			Assert.True(CombatResolver.ResolveNpcAttackDamage(10) > CombatResolver.ResolveNpcAttackDamage(1));
			Assert.Equal(CombatResolver.ResolveNpcAttackDamage(1), CombatResolver.ResolveNpcAttackDamage(0));
		}

		private static WorldEntity Npc(uint handle, float x, float z, uint level = 1, bool hostile = true)
		{
			var e = WorldEntity.ForNpc(handle, 4010, new Vector3(x, 0, z), Vector3.Zero, "Mob", EntityVitals.FromMax(100, 0, 100));
			e.Hostile = hostile;
			return e;
		}

		private static WorldEntity Player(uint handle, float x, float z)
		{
			return new WorldEntity(handle, 4010, true, $"P{handle}", null) { Position = new Vector3(x, 0, z) };
		}
	}
}
