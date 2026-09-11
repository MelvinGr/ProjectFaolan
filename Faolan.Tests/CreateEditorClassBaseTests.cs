using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.World;
using Xunit;

namespace Faolan.Tests
{
	// The create-editor's class-base push (stat 0x0001 / 0x01F9 / 0x01FB), byte-exact against the retail capture.
	//
	// The push is per class, out of RDB 1000511's own rows. Replaying one class's row for every class - the barbarian
	// 11000 / 17200 / 0 a barbarian create dump carries - gives an Assassin a health base 20 too high and a stamina
	// base 58 too low, and a mage a stamina pool its class does not have.
	public class CreateEditorClassBaseTests
	{
		// Blablabla_soldier-guardian_create_random-actions-combat-levelup-questcomplete.tshark.json frame 2613, the
		// StatUpdateIIR body after its 16-byte [len][iir][0xC350][handle] prefix:
		//   [u32 0][u8 3] then (1, 12000) (505, 17250) (507, 0)
		// The record's own [subop][class][handle] head is StatUpdateCodec's, so the assertion compares the built
		// record from that head onwards - the 12 bytes it skips are the ones the codec owns.
		private static readonly byte[] RetailGuardianF2613 =
		[
			0x00, 0x00, 0x00, 0x00, 0x03,
			0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x2E, 0xE0,
			0x00, 0x00, 0x01, 0xF9, 0x00, 0x00, 0x43, 0x62,
			0x00, 0x00, 0x01, 0xFB, 0x00, 0x00, 0x00, 0x00
		];

		// The create-editor instance the capture used - only the head, which the slice below drops, depends on it.
		private const uint CapturedHandle = 0x021662C8;

		[Fact]
		public void GuardianLevel1_ReproducesTheCapturedEditorPush_ByteExact()
		{
			var (health, mana, stamina) = CharacterMaxStats.ClassBaseHundredths(ConanClass.Guardian, 1);

			var classBase = new CharacterStatBlock()
				.SetRaw(CharacterStatId.HealthMax, health)
				.SetRaw(CharacterStatId.StaminaMax, stamina)
				.SetRaw(CharacterStatId.ManaMax, mana);

			Assert.Equal(RetailGuardianF2613, StatUpdateCodec.Build(new StatUpdateIIR(CapturedHandle, classBase))[12..]);
		}

		[Fact]
		public void TheBaseIsPerClass_NotTheBarbarianConstantItUsedToBe()
		{
			// Three classes that disagree, each from RDB 1000511's own row.
			Assert.Equal((9000u, 0u, 23000u), CharacterMaxStats.ClassBaseHundredths(ConanClass.Assassin, 1));
			Assert.Equal((12000u, 0u, 17250u), CharacterMaxStats.ClassBaseHundredths(ConanClass.Guardian, 1));
			Assert.Equal((11000u, 0u, 17200u), CharacterMaxStats.ClassBaseHundredths(ConanClass.Barbarian, 1)); // the row the capture blob carries

			// A mage has no stamina pool at all.
			var (_, necroMana, necroStamina) = CharacterMaxStats.ClassBaseHundredths(ConanClass.Necromancer, 1);
			Assert.Equal(0u, necroStamina);
			Assert.Equal(24000u, necroMana);
		}
	}
}
