using System;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs;
using Faolan.GameServer.Combat;
using Xunit;

namespace Faolan.Tests
{
	// Byte-exact construct of the spellweaving activate-response + fire records vs the captured bytes in
	// NecroL80_mage-necromancer_login_consuming-potions-food-spellweaving (docs/combos.md,
	// Captures/decode_spellweave.py). caster = 0x0802E5D4 throughout; these are the record bytes AFTER the 0x2000
	// carrier's u32 length prefix (the golden hex is the frame body with that prefix stripped).
	public class SpellweaveCodecTests
	{
		[Theory, InlineData(0x37F298u, 640000u, 0x195DA1FEu, "00000000000000000009c40000010000000100000000 0037f298 00000050 00000000 00000000 00000000 00000050 00000000 195da1fe"),
		 InlineData(0x37E5A8u, 1200u, 0x18FC28DEu, "0000000000000000000004b0000100000001000000000037e5a800000050000000000000000000000000000000500000000018fc28de"),
		 InlineData(0x3969D0u, 1000u, 0x18FC210Eu, "0000000000000000000003e800010000000100000000003969d000000050000000000000000000000000000000500000000018fc210e"),
		 InlineData(0x396AE9u, 650u, 0x18FC1CF6u, "00000000000000000000028a0001000000010000000000396ae900000050000000000000000000000000000000500000000018fc1cf6")]
		// The MagicBuffIIR data tail vs the captured trance + component buffs (frames 1644/1646/1648/1659). Passing
		// the captured effect nonce makes each byte-exact; at runtime the server assigns a unique nonce per buff.
		public void BuildBuffData_MatchesCapture(uint spellBase, uint durationCs, uint nonce, string expected)
		{
			var bytes = SpellweaveCodec.BuildBuffData(spellBase, durationCs, nonce);
			Assert.Equal(expected.Replace(" ", ""), Convert.ToHexStringLower(bytes));
		}

		[Theory, InlineData(0x398u, "f98e10b30000c3500802e5d4010000039800007bd4"), InlineData(0x39Bu, "f98e10b30000c3500802e5d4010000039b00007bd4"), InlineData(0x39Au, "f98e10b30000c3500802e5d4010000039a00007bd4"),
		 InlineData(0x39Du, "f98e10b30000c3500802e5d4010000039d00007bd4")]
		// frame#1843
		// frame#1844
		// frame#1845
			// frame#1846
		public void BuildFireStage_Necro_MatchesCapture(uint fireId, string expected)
		{
			var bytes = SpellweaveCodec.BuildFireStage(0x0802E5D4, fireId);
			Assert.Equal(expected, Convert.ToHexStringLower(bytes));
		}

		[Fact]
		public void BuildReactiveList_ConsumedElementZeroed_MatchesCapture()
		{
			// frame#1774: the per-add re-send after weaving element[1] (Arcane Surge) - that slot is zeroed.
			var bytes = SpellweaveCodec.BuildReactiveList(0x0802E5D4, [0x396AE9, 0x000000, 0x37E6D9, 0x396A22, 0x396A3D]);
			Assert.Equal("54c020890000c3500802e5d4000000000100396ae9000000000037e6d900396a2200396a3d", Convert.ToHexStringLower(bytes));
		}

		[Fact]
		public void BuildReactiveList_Necro_MatchesCapture()
		{
			// frame#1647: the activate-response element list - all 5 necro weave elements available.
			var elements = SpellweaveCatalog.ForClass(Class.Necromancer).WeaveElements;
			var bytes = SpellweaveCodec.BuildReactiveList(0x0802E5D4, elements);
			Assert.Equal("54c020890000c3500802e5d4000000000100396ae9003969d80037e6d900396a2200396a3d", Convert.ToHexStringLower(bytes));
		}

		[Fact]
		public void BuildStatGauge_HealthDot_MatchesCapture()
		{
			// the "Spellweaving" health DoT gauge that hits when stamina empties (token 602395, delta -31705, scale 0x1B).
			var bytes = SpellweaveCodec.BuildStatGauge(0x0802E5D4, 602395, -31705, SpellweaveCodec.DotGauge, SpellweaveCodec.DotScale);
			Assert.Equal("e9f241840000c3500802e5d4000009311b0000000000000000000000000000001b00000002ffff8427000000000000c3500802e5d400000001000000000037e69000000050000000000000000000000000", Convert.ToHexStringLower(bytes));
		}

		[Fact]
		public void BuildStatGauge_Stamina_MatchesCapture()
		{
			// frame#1654: the first "Spellweaving Cost" (stamina) gauge tick (token 0x22CE, delta -90).
			var bytes = SpellweaveCodec.BuildStatGauge(0x0802E5D4, 0x22CE, -90, SpellweaveCodec.StaminaGauge, SpellweaveCodec.StaminaScale);
			Assert.Equal("e9f241840000c3500802e5d400000022ce000000000000000000000000000f425c00000002ffffffa6000000000000c3500802e5d400000001000000000037e68c00000050000000000000000000000000", Convert.ToHexStringLower(bytes));
		}
	}
}
