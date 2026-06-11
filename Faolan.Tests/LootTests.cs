using System.Numerics;
using Faolan.GameServer.Combat;
using Faolan.GameServer.World;
using Xunit;

namespace Faolan.Tests
{
	// Loot on kill (docs/server-architecture-roadmap.md): a kill may drop a loot bag (a transient world object) at the
	// death spot that expires off the tick.
	public class LootTests
	{
		[Fact]
		public void RollsDrop_RespectsChance()
		{
			Assert.True(LootResolver.RollsDrop(0.0, 0.5)); // a roll of 0 always drops
			Assert.True(LootResolver.RollsDrop(0.49, 0.5));
			Assert.False(LootResolver.RollsDrop(0.5, 0.5)); // boundary: not under chance
			Assert.False(LootResolver.RollsDrop(0.99, 0.5));
			// chance 0 never drops, chance 1 always drops
			Assert.False(LootResolver.RollsDrop(0.0, 0.0));
			Assert.True(LootResolver.RollsDrop(0.999, 1.0));
		}

		[Fact]
		public void LootBag_ExpiresAfterItsLifetime_AndIsNeitherPlayerNorNpc()
		{
			var bag = WorldEntity.ForLoot(0xABCD, 4010, new Vector3(1, 2, 3), Vector3.Zero, "Bag of loot",
				new byte[] { 1, 2, 3 }, expirySeconds: 5.0);

			Assert.True(bag.IsLoot);
			Assert.False(bag.IsPlayer);
			Assert.False(bag.IsDead); // no vitals
			Assert.NotNull(bag.SpawnBody);

			Assert.False(bag.TickExpiry(2.0)); // 2s
			Assert.False(bag.TickExpiry(2.0)); // 4s
			Assert.True(bag.TickExpiry(2.0)); // 6s >= 5s -> expired
		}

		[Fact]
		public void TickExpiry_NoOpForNonLoot()
		{
			var npc = WorldEntity.ForNpc(1, 4010, Vector3.Zero, Vector3.Zero, "Mob", EntityVitals.FromMax(10, 0, 10));
			Assert.False(npc.TickExpiry(1000.0));
		}

		[Fact]
		public void BuildNewCurrentLooterBody_MatchesCapture()
		{
			// hoxxling-login-gameplay: bag 0x000021FE -> looter player 0x01316E71.
			var body = WorldLoot.BuildNewCurrentLooterBody(0x000021FE, 0x01316E71);
			Assert.Equal(
				"d06568c80000c87a000021fe000000c35001316e71",
				System.Convert.ToHexString(body).ToLowerInvariant());
		}

		[Fact]
		public void BuildFadeBody_MatchesCapture()
		{
			// hoxxling-login-gameplay: fade bag 0x000021FE over 3.0s (0x40400000 big-endian float).
			var body = WorldLoot.BuildFadeBody(WorldLoot.LootTypeTag, 0x000021FE, 3.0f);
			Assert.Equal(
				"66180b200000c87a000021fe0040400000",
				System.Convert.ToHexString(body).ToLowerInvariant());
		}

		[Fact]
		public void WorldStateTick_ExpiresLootAndReportsIt()
		{
			var world = new WorldState();
			var zone = world.ZoneFor(4010);
			var bag = WorldEntity.ForLoot(0x40000001, 4010, Vector3.Zero, Vector3.Zero, "Bag of loot", new byte[] { 9 }, expirySeconds: 1.0);
			zone.Add(bag);

			Assert.Empty(world.Tick(0.5)); // 0.5s, still around
			var effects = world.Tick(0.5); // 1.0s -> expires
			Assert.Single(effects);
			Assert.Same(bag, effects[0].Entity);
			Assert.Equal(WorldTickEffectType.LootExpired, effects[0].Type);
		}
	}
}
