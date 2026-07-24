using System;
using Faolan.Core.Network.Codecs.Combat;
using Xunit;

namespace Faolan.Tests
{
	// Byte-exact construct of the server-originated BehaviorControlCenterIIR (0x642CD3D6) animations vs the captured
	// bytes in NecroL80_mage-necromancer_login_consuming-potions-food-spellweaving (tools/capture/decode_spellweave.py).
	// caster = 0x0802E5D4; the golden hex is the frame body with the 0x2000 carrier's u32 length prefix stripped.
	public class BehaviorAnimationCodecTests
	{
		[Theory, InlineData((ushort)0x0004, "642cd3d60000c3500802e5d40004000000120000001708c5c80210071809280a3000520908d0860310d4cb8b400000c8780001217400000006"),
		 InlineData((ushort)0x000f, "642cd3d60000c3500802e5d4000f000000120000001708c5c80210071809280a3000520908d0860310d4cb8b400000c8780001217400000006")]
		// F2793 (hold-echo, seq 0x0004) / F2801 (hold-assert, seq 0x000f): the object-action STATE that holds the seated pose.
		public void BenchSitState_MatchesCapture(ushort seq, string expected)
		{
			var bytes = BehaviorAnimationCodec.BenchSitState(0x0802E5D4, seq, 0x12174);
			Assert.Equal(expected, Convert.ToHexStringLower(bytes));
		}

		// Sit / stand on a bench - NecroL80_mage-necromancer_login_path-of-azura_sit-bench (tools/capture/decode_sit.py).
		// actor = 0x0802E5D4, target bench handle = 0x12174; the remoteformat is passed verbatim (its key is the bench's
		// captured localized action key).
		[Fact]
		public void PlayerSitOnObject_MatchesCapture()
		{
			// F2795: the sit-on-bench animation ("chair_sit_calm").
			var rf =
				"<remoteformat token=\"startobjectaction(33)|turnon_execute(4)|TavernBenchSimpleDynel\" category=\"50026\" key=\"Q!ss@_3,v&#36;,?,&amp;GEm&quot;[_\" knubot=\"0\"  ></remoteformat>";
			var bytes = BehaviorAnimationCodec.PlayerSitOnObject(0x0802E5D4, 0x12174, rf);
			Assert.Equal(
				"642cd3d60000c3500802e5d400040000002a0000001408c4c802180a28083000520808f8900310f4c20400000002000e63686169725f7369745f63616c6d00000086402762770000000000aa3c72656d6f7465666f726d617420746f6b656e3d2273746172746f626a656374616374696f6e283333297c7475726e6f6e5f657865637574652834297c54617665726e42656e636853696d706c6544796e656c222063617465676f72793d22353030323622206b65793d2251217373405f332c76262333363b2c3f2c26616d703b47456d2671756f743b5b5f22206b6e75626f743d22302220203e3c2f72656d6f7465666f726d61743e0000004c",
				Convert.ToHexStringLower(bytes));
		}

		[Fact]
		public void PlayerStandUp_MatchesCapture()
		{
			// F2840: the stand-up animation ("standup") when the seated player moves.
			var rf = "<remoteformat id=\"0\" category=\"0\" key=\"C4O?^XE&#39;^VC4O?^XE&#39;^V\" knubot=\"0\"  ><parameter value=\"7;Standing up\" /></remoteformat>";
			var bytes = BehaviorAnimationCodec.PlayerStandUp(0x0802E5D4, 0x12174, rf);
			Assert.Equal(
				"642cd3d60000c3500802e5d400040000002a0000001408c5c802180c28083014520808f8900310f4c2040000000000077374616e64757000000000000000000000000000843c72656d6f7465666f726d61742069643d2230222063617465676f72793d223022206b65793d2243344f3f5e5845262333393b5e5643344f3f5e5845262333393b5e5622206b6e75626f743d22302220203e3c706172616d657465722076616c75653d22373b5374616e64696e6720757022202f3e3c2f72656d6f7465666f726d61743e00000001",
				Convert.ToHexStringLower(bytes));
		}

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
		public void SpellweaveTranceExit_MatchesCapture()
		{
			// cancel-weave capture, frame 141.61: the caster's trance-exit animation on cancel/fire.
			var bytes = BehaviorAnimationCodec.SpellweaveTranceExit(0x0802E5D4);
			Assert.Equal("642cd3d60000c3500802e5d40004000000010000000c08d6c202180628083000584f0000000400000027000000000000000000000000", Convert.ToHexStringLower(bytes));
		}

		[Fact]
		public void SpellweaveTrance_MatchesCapture()
		{
			// frame#1645: the caster's trance-pose animation on activate.
			var bytes = BehaviorAnimationCodec.SpellweaveTrance(0x0802E5D4);
			Assert.Equal("642cd3d60000c3500802e5d40004000000010000000d08b9c80218042808300058af090000002700000001000000000000000000000000", Convert.ToHexStringLower(bytes));
		}
	}
}
