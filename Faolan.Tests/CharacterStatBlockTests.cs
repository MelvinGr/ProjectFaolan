using Faolan.GameServer.Packets._0x737A6DF9_Test;
using Xunit;

namespace Faolan.Tests
{
	// The stat-record block is a 1-byte count followed by N × [uint32 statId BE][uint32 value BE], with vitals
	// scaled ×100. Verified against a captured 0x96B8DC59 vitals packet (entity 0x0002324B, Health/Stamina/Mana
	// all 63.00 -> raw 6300 = 0x189C). See docs/quests-and-combat.md.
	public class CharacterStatBlockTests
	{
		[Fact]
		public void Build_VitalsBlock_MatchesCapturedBytes()
		{
			// Captured packet sends the MAX ids (0x1B/0x1F8/0x1FA) - an entity announcing its full vitals on spawn.
			var block = new CharacterStatBlock().Set(LegacyCharacterStatId.HealthMax, 63).Set(LegacyCharacterStatId.StaminaMax, 63).Set(LegacyCharacterStatId.ManaMax, 63);

			Assert.Equal(3, block.Count);

			// count(03) + HealthMax(001B)=189C + StaminaMax(01F8)=189C + ManaMax(01FA)=189C
			Assert.Equal("030000001b0000189c000001f80000189c000001fa0000189c", ToHex(block.ToArray()));
		}

		[Fact]
		public void Set_AppliesVitalsScale()
		{
			// HealthMax is a ×100 vital: display 63 -> raw 6300 (0x189C).
			var block = new CharacterStatBlock().Set(LegacyCharacterStatId.HealthMax, 63);
			Assert.Equal("010000001b0000189c", ToHex(block.ToArray()));
		}

		[Fact]
		public void SetRaw_DoesNotApplyScale()
		{
			var block = new CharacterStatBlock().SetRaw(LegacyCharacterStatId.Level, 25);

			// count(01) + Level(0036) = 25 (0x19), unscaled
			Assert.Equal("010000003600000019", ToHex(block.ToArray()));
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
