using Faolan.GameServer.Combat;
using Faolan.GameServer.Packets._0x737A6DF9_Test;
using Xunit;

namespace Faolan.PacketAnalyzer.Tests
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
		public void ToVitalsBlock_EmitsHealthStaminaMana_AtVitalsScale()
		{
			var v = EntityVitals.FromMax(63, 63, 63);

			// count(03) + Health(001B)=189C + Stamina(01F8)=189C + Mana(01FA)=189C  (63 * 100 = 6300 = 0x189C)
			Assert.Equal("030000001b0000189c000001f80000189c000001fa0000189c", ToHex(v.ToVitalsBlock().ToArray()));
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
		public void CombatState_GetOrAdd_SeedsOnceThenReturnsSameInstance()
		{
			var state = new CombatState();
			var created = 0;

			var first = state.GetOrAdd(0x2C58, () => { created++; return EntityVitals.FromMax(100, 0, 100); });
			first.ApplyHealthDelta(-40);
			var second = state.GetOrAdd(0x2C58, () => { created++; return EntityVitals.FromMax(100, 0, 100); });

			Assert.Equal(1, created);          // factory ran once
			Assert.Same(first, second);        // same tracked entity
			Assert.Equal(60u, second.Health);  // damage persisted in the store

			state.Remove(0x2C58);
			Assert.False(state.TryGet(0x2C58, out _));
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
