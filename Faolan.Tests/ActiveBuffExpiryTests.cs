using System;
using System.Linq;
using Faolan.Core.Network.Constants;
using Faolan.GameServer.Combat;
using Faolan.GameServer.World;
using Xunit;

namespace Faolan.Tests
{
	// WorldEntity.TakeExpiredBuffs - the world tick's buff-expiry step (#274).
	//
	// The regression these guard: the tick used to remove entries while enumerating the live ConcurrentDictionary, so
	// with SEVERAL buffs expiring in the same tick some were skipped (a removal can unlink the bucket neighbour the
	// enumerator was about to visit). Because the client never self-expires an icon, a skipped buff left its icon on
	// the bar at 0s forever. One buff at a time never showed it, which is why it survived the original #274 fix.
	public class ActiveBuffExpiryTests
	{
		[Fact]
		public void ExpiredAndLiveBuffs_Mixed_TakesOnlyTheExpiredOnes()
		{
			var now = DateTimeOffset.UtcNow;
			var entity = Entity();
			for (var i = 0u; i < 20; i++)
			{
				// even ids expired 5s ago, odd ids have 60s left
				entity.ActiveBuffs[0x1000 + i] = new ActiveBuff(0x1000 + i, 7, now.AddSeconds(-10), i % 2 == 0
					? 5f
					: 70f);
			}

			var taken = entity.TakeExpiredBuffs(now);

			Assert.Equal(10, taken.Count);
			Assert.All(taken, b => Assert.Equal(0u, (b.Key - 0x1000) % 2));
			Assert.Equal(10, entity.ActiveBuffs.Count);
			Assert.All(entity.ActiveBuffs.Keys, k => Assert.Equal(1u, (k - 0x1000) % 2));
		}

		[Fact]
		public void ManyBuffsExpiringTogether_AreAllTaken_NoneSkipped()
		{
			var now = DateTimeOffset.UtcNow;
			var entity = Entity();
			// 64 buffs, all long expired. The old remove-while-enumerating shape dropped a subset of these.
			for (var i = 0u; i < 64; i++)
				entity.ActiveBuffs[0x37F0B300 + i] = new ActiveBuff(0x37F0B300 + i, 1, now.AddSeconds(-30), 1f);

			var taken = entity.TakeExpiredBuffs(now);

			Assert.Equal(64, taken.Count);
			Assert.Empty(entity.ActiveBuffs);
			// every id reported exactly once, so the caller sends exactly one end-twin per buff
			Assert.Equal(64, taken.Select(b => b.Key).Distinct().Count());
		}

		[Fact]
		public void BuffWithNoDuration_IsNeverTaken()
		{
			var entity = Entity();
			// DurationSeconds null = lives until cancelled or the session ends (an aura).
			entity.ActiveBuffs[0x4D5FCD00] = new ActiveBuff(0x4D5FCD00, 1, DateTimeOffset.UtcNow.AddHours(-3), null);

			Assert.Empty(entity.TakeExpiredBuffs(DateTimeOffset.UtcNow));
			Assert.Single(entity.ActiveBuffs);
		}

		[Fact]
		public void SecondCall_ReportsNothing_SoNoDuplicateRemovalTwin()
		{
			var now = DateTimeOffset.UtcNow;
			var entity = Entity();
			entity.ActiveBuffs[0x0037F38C] = new ActiveBuff(0x0037F38C, 1, now.AddSeconds(-20), 7f);

			Assert.Single(entity.TakeExpiredBuffs(now));
			Assert.Empty(entity.TakeExpiredBuffs(now));
		}

		[Fact]
		public void BuffExpiringExactlyNow_IsTaken()
		{
			// Expired() is >=, so a buff whose duration has just elapsed goes on this tick rather than the next.
			var now = DateTimeOffset.UtcNow;
			var entity = Entity();
			entity.ActiveBuffs[0x0BEEF00] = new ActiveBuff(0x0BEEF00, 1, now.AddSeconds(-7), 7f);

			Assert.Single(entity.TakeExpiredBuffs(now));
		}

		// The spellweave TRANCE must never be persisted for relog (#690).
		//
		// What happened: a weave was cancelled, which sends the client the buff's end twin - and left the live
		// `ActiveBuffs` entry standing, because `SendBuffRemoval` only wrote packets. The autosave then persisted it
		// (a self-buff no dedicated re-applier owns), and the next login restored it. The player got the trance's
		// EFFECTS and sound looping around them with no weave behind it, no pose (nothing re-sends the trance-enter
		// animation) and no way to stop it - the weave toggle would have opened a new one.
		//
		// Two guards, because the fault needed both: the removal now deregisters (covered by the caller), and the
		// trance is excluded from persistence outright, so logging out mid-weave cannot leave one behind either.
		[Theory]
		[InlineData(0x37F29800u)] // Spellweaving (main)
		[InlineData(0x3969D000u)] // Arcane Drought
		[InlineData(0x37E5A800u)] // Spellweaving(1), stack 0
		[InlineData(0x37E5AA00u)] // ... and further up the stack
		[InlineData(0x000B9000u)] // Exhausted - energy comes back FULL at login, so a restored one is a lie
		public void TheSpellweaveTrance_IsNeverPersisted(uint wireSpellId)
		{
			var entity = Entity();
			var buff = new ActiveBuff(wireSpellId, entity.Handle, DateTimeOffset.UtcNow, 600f);

			Assert.True(SpellweaveCatalog.IsTranceBuff(wireSpellId));
			Assert.False(BuffService.ShouldPersistBuff(entity, buff));
		}

		// The exclusion is the trance, not "buffs that look like it" - an ordinary self-buff still survives a relog,
		// which is the whole point of #274.
		[Theory]
		[InlineData(0x0030A200u)] // an ordinary spell
		[InlineData(0x396AE900u)] // Arcane Renewal - a weave ELEMENT, which is a real spell in its own right
		public void AnOrdinarySelfBuff_IsStillPersisted(uint wireSpellId)
		{
			var entity = Entity();

			Assert.False(SpellweaveCatalog.IsTranceBuff(wireSpellId));
			Assert.True(BuffService.ShouldPersistBuff(entity, new ActiveBuff(wireSpellId, entity.Handle, DateTimeOffset.UtcNow, 600f)));
		}

		private static WorldEntity Entity()
		{
			return new WorldEntity(0x0802E5D4, 4010, true, "Tester", null!);
		}
	}
}
