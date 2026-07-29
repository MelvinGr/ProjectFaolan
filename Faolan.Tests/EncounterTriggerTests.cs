using System.Linq;
using System.Numerics;
using Faolan.GameServer.Combat;
using Faolan.GameServer.World;
using Xunit;

namespace Faolan.Tests
{
	// #502: a proximity-triggered scripted encounter. A harmless LURE watches for an approaching player and, at two
	// measured distances, cries for help and then swaps itself for a boss. Decoded from the Damsel in Distress on
	// Poitain against the capture's own player-movement track: she barked at 21.3 u and the swap fired at 3.0 u.
	//
	// These gate the DECISION (WorldState emits which effect, and when), not the wire side - the swap itself is
	// WorldBroadcaster's and needs a live zone + DB.
	public class EncounterTriggerTests
	{
		private const uint LureTemplate = 13770; // Damsel in Distress
		private const uint BossTemplate = 13774; // Eerika of the Maidens

		[Fact]
		public void ApproachingPlayer_BarksAtTheOuterRadius_ThenTriggersAtTheInner()
		{
			var world = new WorldState();
			var zone = world.ZoneFor(2070);
			var lure = Lure();
			zone.Add(lure);

			// 40 u away: out of both radii, so the lure just idles.
			var player = Player(new Vector3(40, 0, 0));
			zone.Add(player);
			Assert.DoesNotContain(world.Tick(0.1), e => e.Type is WorldTickEffectType.EncounterBark or WorldTickEffectType.EncounterTrigger);

			// 15 u: inside the bark radius, outside the trigger - she calls for help, once.
			player.Position = new Vector3(15, 0, 0);
			var barked = world.Tick(0.1).Where(e => e.Type == WorldTickEffectType.EncounterBark).ToList();
			Assert.Single(barked);
			Assert.Same(lure, barked[0].Entity);
			Assert.Same(player, barked[0].Target);
			Assert.True(lure.EncounterBarked);

			// Still inside it: a bark per tick would be a stream of shouting, so it fires on the APPROACH only.
			player.Position = new Vector3(10, 0, 0);
			Assert.DoesNotContain(world.Tick(0.1), e => e.Type == WorldTickEffectType.EncounterBark);

			// 2 u: the trap springs.
			player.Position = new Vector3(2, 0, 0);
			var sprung = world.Tick(0.1).Where(e => e.Type == WorldTickEffectType.EncounterTrigger).ToList();
			Assert.Single(sprung);
			Assert.Same(lure, sprung[0].Entity);
			Assert.Same(player, sprung[0].Target);
		}

		// A player who closes from out of range to inside the INNER radius between two ticks must spring the trap, not
		// merely be shouted at - at 10 Hz a sprinting player covers several units per tick, so the inner radius can be
		// crossed without ever being sampled between the two.
		[Fact]
		public void PlayerArrivingInsideTheInnerRadius_TriggersWithoutBarkingFirst()
		{
			var world = new WorldState();
			var zone = world.ZoneFor(2070);
			var lure = Lure();
			zone.Add(lure);
			zone.Add(Player(new Vector3(1, 0, 1)));

			var effects = world.Tick(0.1);
			Assert.Contains(effects, e => e.Type == WorldTickEffectType.EncounterTrigger);
			Assert.DoesNotContain(effects, e => e.Type == WorldTickEffectType.EncounterBark);
		}

		// An UNARMED lure is just an NPC. Guards against an encounter silently arming every placement of a type on
		// every map because the definition lookup was skipped.
		[Fact]
		public void AnUnarmedNpc_NeverTriggers()
		{
			var world = new WorldState();
			var zone = world.ZoneFor(2070);
			zone.Add(WorldEntity.ForNpc(1, 2070, Vector3.Zero, Quaternion.Identity, "Damsel in Distress", EntityVitals.FromMax(63, 0, 63), LureTemplate));
			zone.Add(Player(new Vector3(1, 0, 1)));

			Assert.DoesNotContain(world.Tick(0.1), e => e.Type is WorldTickEffectType.EncounterBark or WorldTickEffectType.EncounterTrigger);
		}

		// The boss carries the lure it replaced, so its corpse restores the encounter - once. A second reset would
		// stack a duplicate lure on the spot every time the corpse timer ticked.
		[Fact]
		public void DeadBoss_EmitsExactlyOneResetWhenItsCorpseClears()
		{
			var world = new WorldState();
			var zone = world.ZoneFor(2070);
			var boss = WorldEntity.ForNpc(2, 2070, Vector3.Zero, Quaternion.Identity, "Eerika of the Maidens", EntityVitals.FromMax(8519, 0, 8519), BossTemplate);
			boss.NoRespawn = true; // her corpse fades; the LURE comes back instead
			boss.EncounterLureTemplateId = LureTemplate;
			boss.Vitals!.Health = 0;
			zone.Add(boss);

			// Run well past the corpse/post-death timers, then keep ticking: exactly one reset, ever.
			var resets = world.Tick(60.0).Count(e => e.Type == WorldTickEffectType.EncounterReset);
			resets += world.Tick(60.0).Count(e => e.Type == WorldTickEffectType.EncounterReset);
			Assert.Equal(1, resets);
			Assert.True(boss.EncounterResetDone);
		}

		private static WorldEntity Lure()
		{
			var lure = WorldEntity.ForNpc(1, 2070, Vector3.Zero, Quaternion.Identity, "Damsel in Distress", EntityVitals.FromMax(63, 0, 63), LureTemplate);
			lure.EncounterBossTemplateId = BossTemplate;
			lure.EncounterBarkRadius = 21f;
			lure.EncounterTriggerRadius = 3f;
			return lure;
		}

		private static WorldEntity Player(Vector3 at)
		{
			return new WorldEntity(99, 2070, true, "NecroL80", null!)
			{
				Position = at
			};
		}
	}
}
