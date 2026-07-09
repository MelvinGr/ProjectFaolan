using Faolan.GameServer.Combat;
using Xunit;

namespace Faolan.Tests
{
	// The combat foundation: live entity vitals, the damage resolver, and the in-memory state store. The vitals
	// block these produce is the 0x96B8DC59 update verified byte-for-byte in CharacterStatBlockTests.
	public class CombatTests
	{
		[Fact]
		public void ApplyHealthDelta_ClampsToZeroAndMax()
		{
			var v = EntityVitals.FromMax(100, 0, 100);

			v.ApplyHealthDelta(-30);
			Assert.Equal(70u, v.Health);
			Assert.False(v.IsDead);

			v.ApplyHealthDelta(+1000); // clamp to max
			Assert.Equal(100u, v.Health);

			v.ApplyHealthDelta(-5000); // clamp to zero -> dead
			Assert.Equal(0u, v.Health);
			Assert.True(v.IsDead);
		}

		[Fact]
		public void CombatState_GetOrAdd_SeedsOnceThenReturnsSameInstance()
		{
			var state = new CombatState();
			var created = 0;

			var first = state.GetOrAdd(0x2C58, () =>
			{
				created++;
				return EntityVitals.FromMax(100, 0, 100);
			});
			first.ApplyHealthDelta(-40);
			var second = state.GetOrAdd(0x2C58, () =>
			{
				created++;
				return EntityVitals.FromMax(100, 0, 100);
			});

			Assert.Equal(1, created); // factory ran once
			Assert.Same(first, second); // same tracked entity
			Assert.Equal(60u, second.Health); // damage persisted in the store

			state.Remove(0x2C58);
			Assert.False(state.TryGet(0x2C58, out _));
		}

		[Fact]
		public void NpcMaxHealthForLevel_IsMonotonic_AndExtrapolatesAboveCap()
		{
			Assert.Equal(CombatResolver.NpcMaxHealthForLevel(1), CombatResolver.NpcMaxHealthForLevel(0)); // L0 == L1
			for (uint l = 2; l <= 90; l++)
				Assert.True(CombatResolver.NpcMaxHealthForLevel(l) > CombatResolver.NpcMaxHealthForLevel(l - 1));
			Assert.Equal(3276u + 20 * 53u, CombatResolver.NpcMaxHealthForLevel(80)); // +53/level past the L60 anchor
		}

		// #76: the NPC max-HP fallback (for un-captured creatures) is fitted to the captured civilian baseline.
		[Theory, InlineData(1u, 63u), InlineData(20u, 933u), InlineData(40u, 2217u), InlineData(60u, 3276u), InlineData(35u, 1952u), InlineData(45u, 2481u)]
		// anchor: L1 baseline (27 NPCs)
		// anchor: L20 (43 NPCs)
		// anchor: L40 (109 NPCs)
		// anchor: L60
		// interpolated, captured baseline 1953 (<1%)
		// interpolated, captured baseline 2482 (<1%)
		public void NpcMaxHealthForLevel_MatchesCapturedBaseline(uint level, uint expected)
		{
			Assert.Equal(expected, CombatResolver.NpcMaxHealthForLevel(level));
		}

		[Fact]
		public void Regenerate_AtFull_DoesNothing_AndReportsNoChange()
		{
			var v = EntityVitals.FromMax(100, 100, 100); // current == max

			var changed = v.Regenerate(10, 10, 10);

			Assert.False(changed); // nothing to regen -> caller skips the vitals push
			Assert.Equal(100u, v.Health);
		}

		[Fact]
		public void Regenerate_ClampsAtMax()
		{
			var v = EntityVitals.FromMax(100, 100, 100);
			v.ApplyHealthDelta(-5); // 95/100

			v.Regenerate(50, 50, 50); // would overshoot

			Assert.Equal(100u, v.Health);
			Assert.Equal(100u, v.Stamina);
			Assert.Equal(100u, v.Mana);
		}

		[Fact]
		public void Regenerate_RaisesPoolsTowardMax_AndReportsChange()
		{
			var v = EntityVitals.FromMax(100, 100, 100);
			v.ApplyHealthDelta(-50); // 50/100 hp
			v.ApplyManaDelta(-40); // 60/100 mana
			v.Stamina = 80;

			var changed = v.Regenerate(10, 10, 10);

			Assert.True(changed);
			Assert.Equal(60u, v.Health);
			Assert.Equal(90u, v.Stamina);
			Assert.Equal(70u, v.Mana);
		}

		[Fact]
		public void ResolveKillXp_ScalesWithNpcLevel()
		{
			Assert.Equal(10u + 1 * 25u, CombatResolver.ResolveKillXp(1));
			Assert.Equal(10u + 20 * 25u, CombatResolver.ResolveKillXp(20));
			// Higher-level NPCs are worth more.
			Assert.True(CombatResolver.ResolveKillXp(20) > CombatResolver.ResolveKillXp(5));
			// Level 0 is treated as 1.
			Assert.Equal(CombatResolver.ResolveKillXp(1), CombatResolver.ResolveKillXp(0));
		}

		[Fact]
		public void ResolveSpellDamage_ScalesWithCasterLevel()
		{
			Assert.Equal(20 + 1 * 5, CombatResolver.ResolveSpellDamage(123, 1));
			Assert.Equal(20 + 20 * 5, CombatResolver.ResolveSpellDamage(123, 20));
			// Level 0 is treated as 1.
			Assert.Equal(CombatResolver.ResolveSpellDamage(123, 1), CombatResolver.ResolveSpellDamage(123, 0));
		}

		[Fact]
		public void ReviveToFull_RestoresAllPools()
		{
			var v = EntityVitals.FromMax(100, 50, 80);
			v.ApplyHealthDelta(-100); // dead
			v.ApplyManaDelta(-50);
			Assert.True(v.IsDead);

			v.ReviveToFull();

			Assert.False(v.IsDead);
			Assert.Equal(v.MaxHealth, v.Health);
			Assert.Equal(v.MaxStamina, v.Stamina);
			Assert.Equal(v.MaxMana, v.Mana);
		}

		[Fact]
		public void ToVitalsBlock_EmitsHealthStaminaMana_AtVitalsScale()
		{
			var v = EntityVitals.FromMax(63, 63, 63);

			// Full entity (current == max == 63 -> raw 6300 = 0x189C). Emits the current AND max id per vital, in
			// .Set order (current then max per vital; ids in-client verified, see CharacterStatId):
			// count(06) + HealthCurrent(001B) + HealthMax(0001) + StaminaCurrent(01F8) + StaminaMax(01F9)
			//           + ManaCurrent(01FA) + ManaMax(01FB), each = 0x189C.
			Assert.Equal("060000001b0000189c000000010000189c000001f80000189c000001f90000189c000001fa0000189c000001fb0000189c", ToHex(v.ToVitalsBlock().ToArray()));
		}

		private static string ToHex(byte[] bytes)
		{
			var chars = new char[bytes.Length * 2];
			const string hex = "0123456789abcdef";
			for (var i = 0; i < bytes.Length; i++)
			{
				chars[i * 2] = hex[bytes[i] >> 4];
				chars[i * 2 + 1] = hex[bytes[i] & 0xF];
			}

			return new string(chars);
		}
	}
}
