using System.Linq;
using System.Numerics;
using Faolan.GameServer.Combat;
using Faolan.GameServer.World;
using Xunit;

namespace Faolan.Tests
{
	// NPC aggro + attack AI on the tick (docs/server-architecture-roadmap.md): a living NPC with a player in aggro
	// range swings on a fixed cadence, emitting an NpcAttack effect for the listener to turn into a vitals hit.
	public class NpcAiTests
	{
		private static WorldEntity Npc(uint handle, float x, float z, uint level = 1) =>
			WorldEntity.ForNpc(handle, 4010, new Vector3(x, 0, z), Vector3.Zero, "Mob",
				EntityVitals.FromMax(100, 0, 100));

		private static WorldEntity Player(uint handle, float x, float z) =>
			new WorldEntity(handle, 4010, true, $"P{handle}", null) { Position = new Vector3(x, 0, z) };

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
		public void PlayerInRange_AttacksOnCadence()
		{
			var world = new WorldState();
			var zone = world.ZoneFor(4010);
			var npc = Npc(1, 0, 0);
			var player = Player(2, 5, 0); // within NpcAggroRange (20)
			zone.Add(npc);
			zone.Add(player);

			// First contact swings immediately (cooldown starts at 0), targeting the player.
			var first = world.Tick(0.1);
			var attack = Assert.Single(first);
			Assert.Equal(WorldTickEffectType.NpcAttack, attack.Type);
			Assert.Same(npc, attack.Entity);
			Assert.Same(player, attack.Target);

			// Within the interval, no further swing.
			Assert.Empty(world.Tick(WorldState.NpcAttackInterval / 2));
			// Once the interval elapses, it swings again.
			Assert.Single(world.Tick(WorldState.NpcAttackInterval));
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
			Assert.Same(near, Assert.Single(effects).Target);
		}

		[Fact]
		public void ResolveNpcAttackDamage_ScalesWithLevel()
		{
			Assert.True(CombatResolver.ResolveNpcAttackDamage(10) > CombatResolver.ResolveNpcAttackDamage(1));
			Assert.Equal(CombatResolver.ResolveNpcAttackDamage(1), CombatResolver.ResolveNpcAttackDamage(0));
		}
	}
}
