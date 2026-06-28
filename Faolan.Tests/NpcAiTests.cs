using System.Linq;
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

			// The engage tick also carries the combat-mode (weapon-draw, #218) effect, so select the attack itself.
			var effects = world.Tick(WorldState.NpcAttackInterval);
			Assert.Same(near, effects.Single(e => e.Type == WorldTickEffectType.NpcAttack)
				.Target);
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
		public void InvisiblePlayer_DrawsNoAggro()
		{
			var world = new WorldState();
			var zone = world.ZoneFor(4010);
			zone.Add(Npc(1, 0, 0));
			var player = Player(2, 3, 0); // right next to a hostile NPC
			player.Invisible = true; // .invisible
			zone.Add(player);

			Assert.Empty(world.Tick(WorldState.NpcAttackInterval)); // no aggro -> no attack/combat effects
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

			// First contact swings immediately (cooldown starts at 0), targeting the player. The same engage tick also
			// emits the combat-mode (weapon-draw, #218) effect, so pick the attack out of the pair.
			var first = world.Tick(0.1);
			var attack = first.Single(e => e.Type == WorldTickEffectType.NpcAttack);
			Assert.Same(npc, attack.Entity);
			Assert.Same(player, attack.Target);

			// Within the interval, no further swing.
			Assert.Empty(world.Tick(WorldState.NpcAttackInterval / 2));
			// Once the interval elapses, it swings again.
			Assert.Single(world.Tick(WorldState.NpcAttackInterval));
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

			// The engage tick also carries the combat-mode (weapon-draw, #218) effect alongside the chase step.
			var move = world.Tick(0.5).Single(e => e.Type == WorldTickEffectType.NpcMove);
			Assert.Same(npc, move.Entity);
			Assert.Same(player, move.Target);

			// It stepped toward the player (closer than it started, not past them) and the broadcast segment recorded its
			// origin (the from of the MoveFrom -> Position segment the client interpolates, #272).
			Assert.True(npc.Position.X > 0 && npc.Position.X < 10);
			Assert.Equal(0f, npc.MoveFrom.X);
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
		public void EnterCombat_EffectCarriesAggroTarget()
		{
			var world = new WorldState();
			var zone = world.ZoneFor(4010);
			var npc = Npc(1, 0, 0);
			var player = Player(2, 3, 0);
			zone.Add(npc);
			zone.Add(player);

			// The engage tick emits NpcEnterCombat carrying the aggroed target (so the broadcaster can push HateList/
			// ThreatLevel, #216).
			var enter = world.Tick(WorldState.NpcAttackInterval).Single(e => e.Type == WorldTickEffectType.NpcEnterCombat);
			Assert.Same(player, enter.Target);
		}

		[Fact]
		public void ArrivesHome_EmitsNpcStopped()
		{
			var world = new WorldState();
			var zone = world.ZoneFor(4010);
			var npc = Npc(1, 0, 0);
			npc.Position = new Vector3(WorldState.NpcLeashRange + 5f, 0, 0); // chased out past the leash
			zone.Add(npc);
			zone.Add(Player(2, WorldState.NpcLeashRange + 6f, 0));

			world.Tick(0.5); // flips to returning
			var stopped = false;
			for (var i = 0; i < 400 && !stopped; i++)
				foreach (var e in world.Tick(0.5))
					stopped |= e.Type == WorldTickEffectType.NpcStopped && e.Entity == npc;

			Assert.True(stopped, "an NPC arriving home should emit NpcStopped (#190)");
			Assert.False(npc.Returning);
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
