using System;
using Faolan.GameServer.Combat;
using Xunit;
using Faolan.Core.Network.Codecs.GameServer;

namespace Faolan.Tests
{
	// Byte-exact construct of the server-originated BehaviorControlCenterIIR (0x642CD3D6) animations vs the captured
	// bytes in NecroL80_mage-necromancer_login_consuming-potions-food-spellweaving (Captures/decode_spellweave.py).
	// caster = 0x0802E5D4; the golden hex is the frame body with the 0x2000 carrier's u32 length prefix stripped.
	public class BehaviorAnimationCodecTests
	{
		[Theory, InlineData(false, "642cd3d60000c3500802e5d40004000000040000001508dfb00b180528083000520908d0860310d4cb8b400000000100396ae90100000000000000000000000000"),
		 InlineData(true, "642cd3d60000c3500802e5d4000a000000040000001508dfb00b180528083000520908d0860310d4cb8b400000000100396ae90100000000000000000000000001")]
		// frame#1657 begin
			// frame#1658 complete
		public void SpellweaveComponentAdd_MatchesCapture(bool complete, string expected)
		{
			// the add gesture for element[0] = Arcane Renewal 0x396AE9.
			var bytes = BehaviorAnimationCodec.SpellweaveComponentAdd(0x0802E5D4, 0x396AE9, complete);
			Assert.Equal(expected, Convert.ToHexStringLower(bytes));
		}

		[Fact]
		public void SpellweaveTrance_MatchesCapture()
		{
			// frame#1645: the caster's trance-pose animation on activate.
			var bytes = BehaviorAnimationCodec.SpellweaveTrance(0x0802E5D4);
			Assert.Equal("642cd3d60000c3500802e5d40004000000010000000d08b9c80218042808300058af090000002700000001000000000000000000000000", Convert.ToHexStringLower(bytes));
		}

		[Fact]
		public void SpellweaveTranceExit_MatchesCapture()
		{
			// cancel-weave capture, frame 141.61: the caster's trance-exit animation on cancel/fire.
			var bytes = BehaviorAnimationCodec.SpellweaveTranceExit(0x0802E5D4);
			Assert.Equal("642cd3d60000c3500802e5d40004000000010000000c08d6c202180628083000584f0000000400000027000000000000000000000000", Convert.ToHexStringLower(bytes));
		}
	}
}
