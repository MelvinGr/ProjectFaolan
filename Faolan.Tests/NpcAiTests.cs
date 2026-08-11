using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Faolan.Core.Database.Tables;
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

			Assert.DoesNotContain(world.Tick(1.0), e => e.Type == WorldTickEffectType.NpcAmbientAnimation);
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

		// #804: retail's `AIDefaults_ChaseDeaggroDelayTime` (5) - a mob that loses its target holds its ground for the
		// delay before turning for home, so stepping out of aggro range and straight back in is ONE engagement.
		[Fact]
		public void LosingTheTarget_DoesNotTurnForHomeUntilTheDeaggroDelayHasPassed()
		{
			var world = new WorldState();
			var zone = world.ZoneFor(4010);
			var npc = NpcTemplate(1, 0, 0); // homes at the origin
			npc.Position = new Vector3(10, 0, 0); // chased out, but well inside the leash
			zone.Add(npc);

			world.Tick(0.5); // no player anywhere: the target is lost, and the clock starts
			Assert.NotNull(npc.TargetLostAt);
			Assert.False(npc.Returning, "a mob that just lost its target should hold its ground, not turn for home");

			// A player walking back into range inside the delay clears the stamp - one engagement, not two.
			var player = Player(2, 11, 0);
			zone.Add(player);
			world.Tick(0.5);
			Assert.Null(npc.TargetLostAt);
			Assert.False(npc.Returning);

			// Gone again, and this time the delay elapses.
			zone.Remove(player.Handle);
			world.Tick(0.5);
			npc.TargetLostAt = npc.TargetLostAt!.Value.AddSeconds(-WorldState.NpcChaseDeaggroDelaySeconds);
			world.Tick(0.5);
			Assert.True(npc.Returning, "past the deaggro delay the mob should head home");
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
			// #225/#766: an ordinary NPC's idle behaviour is now the ambient ANIMATION, on its own per-entity clock -
			// there is no per-tick heartbeat pulse to fire at all. Non-hostile so no aggro/move effects muddy it.
			var world = new WorldState();
			var zone = world.ZoneFor(4010);
			var npc = NpcTemplate(1, 0, 0, hostile: false);
			npc.AmbientAnimations = [new AmbientAnimation(62171, 200, 10)];
			zone.Add(npc);

			Assert.Single(world.Tick(1.0), e => e.Type == WorldTickEffectType.NpcAmbientAnimation && e.Entity == npc);
			// ...and the next is not due until this one has finished (2s) plus the pause it then stands for.
			Assert.DoesNotContain(world.Tick(1.0), e => e.Type == WorldTickEffectType.NpcAmbientAnimation);
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
		public void NonHostileNpc_DoesNotAttack()
		{
			var world = new WorldState();
			var zone = world.ZoneFor(4010);
			zone.Add(NpcTemplate(1, 0, 0, hostile: false)); // a quest-giver / vendor
			zone.Add(Player(2, 3, 0)); // right next to it

			Assert.Empty(NonHeartbeat(world.Tick(WorldState.NpcAttackInterval)));
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
			Assert.True(CombatResolver.ResolveNpcAttackDamage(10, 10) > CombatResolver.ResolveNpcAttackDamage(1, 1));
			Assert.Equal(CombatResolver.ResolveNpcAttackDamage(1, 1), CombatResolver.ResolveNpcAttackDamage(0, 0));
		}

		// #572: an NPC's swing must be in the SAME unit as the pool it lands in. It was not - the curve is measured in
		// wire units (level * 100) while every live pool is the ÷100 display value - so a level-1 mob swung 100 into a
		// 63-point pool and a level-3 swung 300 into a level-1 player's 88, killing on contact. The test above passed
		// throughout: monotonic-and-floored is true on either scale, which is exactly how this shipped. This pins the
		// RATIO instead, which is the thing that was wrong and the thing retail actually measured (~1.5% of a
		// same-level pool per hit).
		[Fact]
		public void ResolveNpcAttackDamage_IsASmallFractionOfASameLevelPool_NotWireUnits()
		{
			Assert.Equal(1u, CombatResolver.ResolveNpcAttackDamage(1, 1)); // display units: 1, not the wire 100

			foreach (var level in new uint[] { 1, 2, 3, 5, 10, 20, 40, 60 })
			{
				var swing = CombatResolver.ResolveNpcAttackDamage(level, level);
				var pool = CombatResolver.NpcMaxHealthForLevel(level);

				// A same-level fight lasts tens of hits, not one. On the wire scale every one of these is >= 100% and
				// the whole range fails, which is the regression this exists to catch.
				Assert.InRange(swing * 100.0 / pool, 0.5, 5.0);
			}
		}

		// #488: the level-GAP penalty, pinned to the measurement it was built from rather than to its own constants.
		// Each row is (mob level, player level, the measured `x model` band from `--report npc-damage`'s BY ATTACKER
		// section) - so if the table is ever retuned, this fails against RETAIL, not against a copy of the code.
		//
		// The gap-6 row is the loose one on purpose: it is one template's 11 hits at 0.42, with a single 0.76 sighting
		// from an above-curve archetype beside it.
		[Theory]
		// Same-level and near-level: full damage. 11 beach templates measure 0.77-1.57 at gap 0-1.
		[InlineData(1u, 1u, 0.9, 1.1)]
		[InlineData(5u, 6u, 0.9, 1.1)]
		// Gap 5: three templates at level 75 against a level-80 player measure 0.96 / 0.99 / 1.35 - still full.
		[InlineData(75u, 80u, 0.9, 1.1)]
		// Gap 6: the one step. Templates 15441 / 15434 at level 74 measure 0.42 (11 hits) and 0.76 (1 hit).
		[InlineData(74u, 80u, 0.35, 0.8)]
		// Gap 7 through 21: the floor. 0.10-0.14 over 39 hits and six templates.
		[InlineData(73u, 80u, 0.08, 0.16)]
		[InlineData(70u, 80u, 0.08, 0.16)]
		[InlineData(59u, 80u, 0.08, 0.16)]
		// A mob ABOVE the player is deliberately un-penalised (two single-hit rows are not enough to amplify on).
		[InlineData(42u, 23u, 0.9, 1.1)]
		public void ResolveNpcAttackDamage_ReproducesTheMeasuredLevelGapCurve(uint npcLevel, uint playerLevel, double low, double high)
		{
			// The measured ratio is over `level * 100` in WIRE units; the resolver answers in display units, so the
			// comparison is against the same curve in display units.
			var unpenalised = CombatResolver.ResolveNpcAttackDamage(npcLevel, npcLevel);
			var actual = CombatResolver.ResolveNpcAttackDamage(npcLevel, playerLevel);

			Assert.InRange((double)actual / unpenalised, low, high);
		}

		// #671: a mob that SHOOTS deals about a fifth of what one that swings does, and the factor is independent of
		// the level gap. Pinned to the three well-sampled bow templates (`Poacher Marksman` 8758 at 0.17 over 31
		// hits, 13961 at 0.18 over 18, `Bat Pict Archer` 937 at 0.21 over 70 - all at gap 0-1, where the per-level
		// curve itself is verified).
		[Theory]
		[InlineData(20u, 20u)]
		[InlineData(40u, 40u)]
		[InlineData(80u, 80u)]
		[InlineData(75u, 80u)] // and it composes with the level gap rather than replacing it
		public void ARangedMob_DealsAboutAFifthOfAMeleeOneOfTheSameLevel(uint npcLevel, uint playerLevel)
		{
			var melee = CombatResolver.ResolveNpcAttackDamage(npcLevel, playerLevel);
			var ranged = CombatResolver.ResolveNpcAttackDamage(npcLevel, playerLevel, true);

			Assert.InRange((double)ranged / melee, 0.15, 0.25);
		}

		// ⚠️ The ratio cannot hold at the BOTTOM of the range, and that is the floor doing its job rather than the
		// factor failing: a level-3 archer's fifth is 0.57 of a display point, and a hit that lands for 0 reads as a
		// miss. So the low levels are asserted as "still lands, still softer than a swing" instead.
		[Theory]
		[InlineData(1u)]
		[InlineData(2u)]
		[InlineData(3u)]
		public void ALowLevelRangedMob_StillLandsForAtLeastOne(uint level)
		{
			var ranged = CombatResolver.ResolveNpcAttackDamage(level, level, true);

			Assert.True(ranged >= 1);
			Assert.True(ranged <= CombatResolver.ResolveNpcAttackDamage(level, level));
		}

		// The archetype is read off the item CATALOG, never off a name (#244): three unrelated bow names, one answer.
		[Theory]
		[InlineData(30017u, 6u, true)] // bow
		[InlineData(30018u, 6u, true)] // crossbow
		[InlineData(30019u, 6u, true)] // thrown
		[InlineData(30011u, 6u, false)] // 1h edge - the common melee case
		[InlineData(30016u, 6u, false)] // polearm
		[InlineData(30017u, 8u, false)] // a bow in the OFF hand is not what the mob attacks with
		public void NpcWeapon_ReadsTheRangedArchetypeFromTheItemCategory(uint category, uint pos, bool expected)
		{
			var equipment = new[]
			{
				new NpcSpawnEquipment { Pos = pos, Template = 1, Item = new Item { Id = 1, Name = "whatever", SearchCategory = category } }
			};

			Assert.Equal(expected, NpcWeapon.IsRanged(equipment));
		}

		[Fact]
		public void NpcWeapon_AnUnarmedOrAnimalNpc_IsNotRanged()
		{
			Assert.False(NpcWeapon.IsRanged(null));
			Assert.False(NpcWeapon.IsRanged([]));
		}

		// The regression that motivated the ticket, stated in the terms it was reported in: a level-80 player wading
		// through level-70 mobs used to take TEN TIMES what retail deals. Not a restatement of the table above - this
		// is the consequence, in the pool the damage actually lands in.
		[Fact]
		public void ALevel70Mob_BarelyScratchesALevel80Player()
		{
			var swing = CombatResolver.ResolveNpcAttackDamage(70, 80);
			var pool = CombatResolver.NpcMaxHealthForLevel(80); // a level-80 pool, standing in for the player's

			Assert.InRange(swing * 100.0 / pool, 0.05, 0.5); // fractions of a percent a hit, not ~2%
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

		// The ambient idle animation (#766) fires on each NPC's own clock, so the "no aggro/attack happened" assertions
		// filter it out - they care that no COMBAT effect was produced, not that the tick was silent.
		private static IEnumerable<WorldTickEffect> NonHeartbeat(IReadOnlyList<WorldTickEffect> effects)
		{
			return effects.Where(e => e.Type != WorldTickEffectType.NpcAmbientAnimation);
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
