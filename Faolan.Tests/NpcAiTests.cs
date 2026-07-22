using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Faolan.GameServer.Combat;
using Faolan.GameServer.World;
using Xunit;

namespace Faolan.Tests
{
	// NPC aggro + attack AI on the tick (Gitea #404): a living NPC with a player in aggro
	// range swings on a fixed cadence, emitting an NpcAttack effect for the listener to turn into a vitals hit.
	public class NpcAiTests
	{
		[Fact]
		public void ArrivesHome_EmitsNpcStopped()
		{
			var world = new WorldState();
			var zone = world.ZoneFor(4010);
			var npc = NpcTemplate(1, 0, 0);
			npc.Position = new Vector3(WorldState.NpcLeashRange + 5f, 0, 0); // chased out past the leash
			zone.Add(npc);
			zone.Add(Player(2, WorldState.NpcLeashRange + 6f, 0));

			world.Tick(0.5); // flips to returning
			var stopped = false;
			for (var i = 0; i < 400 && !stopped; i++)
			{
				foreach (var e in world.Tick(0.5))
					stopped |= e.Type == WorldTickEffectType.NpcStopped && e.Entity == npc;
			}

			Assert.True(stopped, "an NPC arriving home should emit NpcStopped (#190)");
			Assert.False(npc.Returning);
		}

		[Fact]
		public void AttacksNearestPlayer()
		{
			var world = new WorldState();
			var zone = world.ZoneFor(4010);
			var npc = NpcTemplate(1, 0, 0);
			var far = Player(2, 15, 0);
			var near = Player(3, 3, 0);
			zone.Add(npc);
			zone.Add(far);
			zone.Add(near);

			// The engage tick also carries the combat-mode (weapon-draw, #218) effect, so select the attack itself.
			var effects = world.Tick(WorldState.NpcAttackInterval);
			Assert.Same(near, effects.Single(e => e.Type == WorldTickEffectType.NpcAttack).Target);
		}

		[Fact]
		public void Chase_ArrivesInMeleeRangeThenAttacks()
		{
			var world = new WorldState();
			var zone = world.ZoneFor(4010);
			var npc = NpcTemplate(1, 0, 0);
			zone.Add(npc);
			zone.Add(Player(2, 8, 0));

			// Several seconds of chasing closes the gap; eventually a swing lands once inside melee range.
			var sawAttack = false;
			for (var i = 0; i < 40 && !sawAttack; i++)
			{
				foreach (var e in world.Tick(0.5))
					sawAttack |= e.Type == WorldTickEffectType.NpcAttack;
			}

			Assert.True(sawAttack);
		}

		[Fact]
		public void DamagedNpc_DoesNotHealWhilePlayerInRange()
		{
			var world = new WorldState();
			var zone = world.ZoneFor(4010);
			var npc = NpcTemplate(1, 0, 0);
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
			var npc = NpcTemplate(1, 0, 0);
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
			var npc = NpcTemplate(1, 0, 0);
			npc.Vitals.ApplyHealthDelta(-100); // dead
			npc.MarkDead();
			zone.Add(npc);
			zone.Add(Player(2, 5, 0));

			// No attack from a corpse (it counts down to respawn instead).
			Assert.DoesNotContain(world.Tick(WorldState.NpcAttackInterval), e => e.Type == WorldTickEffectType.NpcAttack);
		}

		[Fact]
		public void DeadNpc_EmitsNoHeartbeat()
		{
			// #225: a dead NPC awaiting respawn must NOT heartbeat - its handle is being torn down, and a pulse for a
			// corpse the client is despawning would reference a handle in flux. The emit sits past the IsDead gate.
			var world = new WorldState();
			var zone = world.ZoneFor(4010);
			var npc = NpcTemplate(1, 0, 0, hostile: false);
			npc.Vitals.ApplyHealthDelta(-100); // killing blow
			npc.MarkDead();
			zone.Add(npc);

			Assert.DoesNotContain(world.Tick(1.0), e => e.Type == WorldTickEffectType.NpcHeartbeat);
		}

		[Fact]
		public void EnterCombat_EffectCarriesAggroTarget()
		{
			var world = new WorldState();
			var zone = world.ZoneFor(4010);
			var npc = NpcTemplate(1, 0, 0);
			var player = Player(2, 3, 0);
			zone.Add(npc);
			zone.Add(player);

			// The engage tick emits NpcEnterCombat carrying the aggroed target (so the broadcaster can push HateList/
			// ThreatLevel, #216).
			var enter = world.Tick(WorldState.NpcAttackInterval).Single(e => e.Type == WorldTickEffectType.NpcEnterCombat);
			Assert.Same(player, enter.Target);
		}

		[Fact]
		public void InvisiblePlayer_DrawsNoAggro()
		{
			var world = new WorldState();
			var zone = world.ZoneFor(4010);
			zone.Add(NpcTemplate(1, 0, 0));
			var player = Player(2, 3, 0); // right next to a hostile NPC
			player.Invisible = true; // .invisible
			zone.Add(player);

			Assert.Empty(NonHeartbeat(world.Tick(WorldState.NpcAttackInterval))); // no aggro -> no attack/combat effects
		}

		[Fact]
		public void LeashedNpc_StopsChasingAndReturnsHome()
		{
			var world = new WorldState();
			var zone = world.ZoneFor(4010);
			var npc = NpcTemplate(1, 0, 0); // spawns (and homes) at the origin
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
		public void LiveNpc_EmitsHeartbeat_OncePerInterval()
		{
			// #225: a live NPC pulses its idle heartbeat on its own cadence (HeartbeatIntervalSeconds), not every base
			// tick and not before the interval elapses. Non-hostile so no aggro/move effects muddy the assertion.
			var world = new WorldState();
			var zone = world.ZoneFor(4010);
			var npc = NpcTemplate(1, 0, 0, hostile: false);
			zone.Add(npc);

			Assert.DoesNotContain(world.Tick(0.2), e => e.Type == WorldTickEffectType.NpcHeartbeat); // before the interval
			Assert.Single(world.Tick(1.0), e => e.Type == WorldTickEffectType.NpcHeartbeat && e.Entity == npc); // after
		}

		[Fact]
		public void NonHostileNpc_DoesNotAttack()
		{
			var world = new WorldState();
			var zone = world.ZoneFor(4010);
			zone.Add(NpcTemplate(1, 0, 0, hostile: false)); // a quest-giver / vendor
			zone.Add(Player(2, 3, 0)); // right next to it

			Assert.Empty(NonHeartbeat(world.Tick(WorldState.NpcAttackInterval)));
		}

		[Fact]
		public void NoPlayerInRange_NoAttack()
		{
			var world = new WorldState();
			var zone = world.ZoneFor(4010);
			zone.Add(NpcTemplate(1, 0, 0));
			zone.Add(Player(2, 1000, 1000)); // far away

			Assert.Empty(NonHeartbeat(world.Tick(WorldState.NpcAttackInterval * 2)));
		}

		[Fact]
		public void PlayerBeyondMeleeRange_ChasesTowardPlayer()
		{
			var world = new WorldState();
			var zone = world.ZoneFor(4010);
			var npc = NpcTemplate(1, 0, 0);
			var player = Player(2, 10, 0); // aggro range (20) but beyond melee (3)
			zone.Add(npc);
			zone.Add(player);

			// The engage tick also carries the combat-mode (weapon-draw, #218) effect alongside the chase step, and a
			// chase MoveTask is only broadcast once the NPC has accumulated a retail-sized segment (#272 throttle:
			// MoveBroadcastSegment), so a single 0.5s step (3.5u) is below threshold - tick until the first move fires.
			WorldTickEffect? move = null;
			for (var i = 0; i < 10 && move == null; i++)
			{
				foreach (var e in world.Tick(0.5))
				{
					if (e.Type == WorldTickEffectType.NpcMove)
						move ??= e;
				}
			}

			Assert.NotNull(move);
			Assert.Same(npc, move.Value.Entity);
			Assert.Same(player, move.Value.Target);

			// It stepped toward the player (closer than it started, not past them) and the broadcast segment recorded its
			// origin (the from of the MoveFrom -> Position segment the client interpolates, #272).
			Assert.True(npc.Position.X is > 0 and < 10);
			Assert.Equal(0f, npc.MoveFrom.X);
		}

		[Fact]
		public void PlayerInMeleeRange_AttacksOnCadence()
		{
			var world = new WorldState();
			var zone = world.ZoneFor(4010);
			var npc = NpcTemplate(1, 0, 0);
			var player = Player(2, 2, 0); // within NpcMeleeRange (3)
			zone.Add(npc);
			zone.Add(player);

			// First contact swings immediately (cooldown starts at 0), targeting the player. The same engage tick also
			// emits the combat-mode (weapon-draw, #218) effect, so pick the attack out of the pair.
			var first = world.Tick(0.1);
			var attack = first.Single(e => e.Type == WorldTickEffectType.NpcAttack);
			Assert.Same(npc, attack.Entity);
			Assert.Same(player, attack.Target);

			// Within the interval, no further swing. (The mob may still flush a trailing walk-up step toward contact
			// range, #272 - that's an NpcMove, not a swing - so assert on the attack itself, not on emptiness.)
			Assert.DoesNotContain(world.Tick(WorldState.NpcAttackInterval / 2), e => e.Type == WorldTickEffectType.NpcAttack);
			// Once the interval elapses, it swings again.
			Assert.Single(world.Tick(WorldState.NpcAttackInterval), e => e.Type == WorldTickEffectType.NpcAttack);
		}

		[Fact]
		public void PlayerOnDifferentFloor_DrawsNoAggro()
		{
			var world = new WorldState();
			var zone = world.ZoneFor(4010);
			zone.Add(NpcTemplate(1, 0, 0));
			// Right above the NPC (1u horizontal) but a storey up - through a floor (#284 vertical-delta gate).
			var player = new WorldEntity(2, 4010, true, "P2", null!)
			{
				Position = new Vector3(1, WorldState.NpcAggroVerticalRange + 2f, 0)
			};
			zone.Add(player);

			Assert.Empty(NonHeartbeat(world.Tick(WorldState.NpcAttackInterval))); // no aggro across the vertical gap -> no effects
		}

		[Fact]
		public void ResolveNpcAttackDamage_ScalesWithLevel()
		{
			Assert.True(CombatResolver.ResolveNpcAttackDamage(10) > CombatResolver.ResolveNpcAttackDamage(1));
			Assert.Equal(CombatResolver.ResolveNpcAttackDamage(1), CombatResolver.ResolveNpcAttackDamage(0));
		}

		[Fact]
		public void Respawn_ResetsToOriginalSpawnPoint()
		{
			var world = new WorldState();
			var zone = world.ZoneFor(4010);
			var npc = NpcTemplate(1, 0, 0); // homes at the origin
			npc.RespawnDelaySeconds = 1.0;
			zone.Add(npc);

			// It died well away from its spawn (chased out, killed at range).
			npc.Position = new Vector3(50f, 0, 30f);
			npc.Vitals.ApplyHealthDelta(-100);
			npc.MarkDead();

			var respawned = false;
			for (var i = 0; i < 100 && !respawned; i++)
			{
				foreach (var e in world.Tick(0.5))
					respawned |= e.Type == WorldTickEffectType.NpcRespawned && e.Entity == npc;
			}

			Assert.True(respawned, "a dead NPC should respawn once its delay elapses");
			// It reappears at its ORIGINAL spawn point, not where it was killed (#246).
			Assert.Equal(npc.SpawnPosition, npc.Position);
			Assert.Equal(npc.SpawnPosition, npc.MoveFrom);
		}

		// The idle heartbeat (#225) fires for every live NPC on its own cadence, so the "no aggro/attack happened"
		// assertions filter it out - they care that no COMBAT effect was produced, not that the tick was silent.
		private static IEnumerable<WorldTickEffect> NonHeartbeat(IReadOnlyList<WorldTickEffect> effects)
		{
			return effects.Where(e => e.Type != WorldTickEffectType.NpcHeartbeat);
		}

		private static WorldEntity NpcTemplate(uint handle, float x, float z, uint level = 1, bool hostile = true)
		{
			var e = WorldEntity.ForNpc(handle, 4010, new Vector3(x, 0, z), Quaternion.Identity, "Mob", EntityVitals.FromMax(100, 0, 100));
			e.Hostile = hostile;
			return e;
		}

		private static WorldEntity Player(uint handle, float x, float z)
		{
			return new WorldEntity(handle, 4010, true, $"P{handle}", null!)
			{
				Position = new Vector3(x, 0, z)
			};
		}
	}
}
