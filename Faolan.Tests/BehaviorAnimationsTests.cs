using System;
using System.Text;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.World;
using Xunit;

namespace Faolan.Tests
{
	// Byte-exact construct of the server-originated BehaviorControlCenterIIR (0x642CD3D6) animations vs the captured
	// bytes in NecroL80_mage-necromancer_login_consuming-potions-food-spellweaving (tools/capture/decode_spellweave.py).
	// caster = 0x0802E5D4; the golden hex is the frame body with the 0x2000 carrier's u32 length prefix stripped.
	public class BehaviorAnimationsTests
	{
		// #676: a ranged NPC's swing, byte-exact against the pair retail sent for ONE shot -
		// Hoxxling_mage-heraldofxotli_login_gameplay frames 9017 (begin) and 9038 (complete). The two differ in the
		// sequence word and in nothing else: same swing counter, same args, byte-identical 82-byte tail.
		//
		// It is a whole SHOT rather than one record on purpose. Our archers used to send a single `Combat:Melee`
		// (0x0F) ack - a sword swing for a bow, and only the opening half of the action - which is what this issue is.
		[Theory]
		[InlineData(BehaviorAnimations.RangedBeginSeq,
			"642cd3d60000c35000017e160004000000100000001908f8df04180328183000520908d0860310f1dcc50958536051" +
			"000000000000000000000001000000000000000000000000000000000000000000000000000000010000c35001316e710000000006000000cd00000000000000010000003f00000002000000000000000000")]
		[InlineData(BehaviorAnimations.RangedCompleteSeq,
			"642cd3d60000c35000017e16000a000000100000001908f8df04180328183000520908d0860310f1dcc50958536051" +
			"000000000000000000000001000000000000000000000000000000000000000000000000000000010000c35001316e710000000006000000cd00000000000000010000003f00000002000000000000000000")]
		public void ANpcShot_MatchesCapture(ushort sequence, string expected)
		{
			var bytes = BehaviorControlCenterCodec.Build(BehaviorAnimations.NpcAttackAck(0x00017E16, 0x01316E71, 63, 77816, 3, sequence, BehaviorId.CombatRanged, 24, 83, 81, 0xCD));

			Assert.Equal(expected, Convert.ToHexStringLower(bytes));
		}

		// #827: FREEING Casilda takes TWO records, and we shipped one - so she followed the player still playing the
		// chained struggle. A registration is per SLOT and the struggle lives in slot 5, so writing her locomotion
		// into slot 9 ADDS a behaviour rather than replacing the looping one.
		//
		// Retail's rescue is three consecutive records on her handle 0x00017ED0 in
		// Hoxxling_mage-heraldofxotli_login_gameplay - idx 2104 (slot 7, an end stamp), 2106 (slot 9, locomotion) and
		// 2110 (slot 5, 142007). This pins the one that was missing, byte-exact against frame 1847.
		//
		// ⚠️ Its sibling 2106 is deliberately NOT pinned here: the captured record carries a full behaviour-state tail
		// (six NullIdentity pairs + three trailing ones, 124 bytes) where we send the short 20-byte bound form. That
		// difference predates this fix and she does walk on it, so it is recorded rather than quietly "corrected" -
		// widening it is a byte-exact job of its own.
		[Fact]
		public void CasildasFreedSlot5Behaviour_MatchesCapture()
		{
			var bytes = BehaviorControlCenterCodec.Build(BehaviorAnimations.Registration(0x00017ED0, 5, 142007, 8, 24, 0, field12: 260));

			Assert.Equal("642cd3d60000c35000017ed00004000000050000000d08b7d508180828183000608402", Convert.ToHexString(bytes).ToLowerInvariant());
		}


		// The one thing that separates the ranged result block from the melee one: a single trailing 0x00. That is
		// the whole reason every captured ranged tail is one byte longer than the melee tail of the same shape
		// (82 against 81 with one result), and getting it wrong makes the client read past the record.
		[Fact]
		public void TheRangedResultBlock_IsTheMeleeOnePlusOneTrailingZero()
		{
			var melee = BehaviorControlCenterCodec.Build(BehaviorAnimations.NpcAttackAck(0x00017E16, 0x01316E71, 63, 77816, 3, BehaviorAnimations.RangedBeginSeq, BehaviorId.CombatMelee, 24, 83,
				81, 0xCD));
			var ranged = BehaviorControlCenterCodec.Build(BehaviorAnimations.NpcAttackAck(0x00017E16, 0x01316E71, 63, 77816, 3, BehaviorAnimations.RangedBeginSeq, BehaviorId.CombatRanged, 24, 83,
				81, 0xCD));

			Assert.Equal(melee.Length + 1, ranged.Length);
			Assert.Equal(0x00, ranged[^1]);
			// ...and the only other difference is the behaviour id itself, four bytes in at a known offset.
			Assert.Equal((uint)BehaviorId.CombatMelee, BehaviorControlCenterCodec.Parse(melee).BehaviorId);
			Assert.Equal((uint)BehaviorId.CombatRanged, BehaviorControlCenterCodec.Parse(ranged).BehaviorId);
		}

		[Theory, InlineData((ushort)0x0004, "642cd3d60000c3500802e5d40004000000120000001708c5c80210071809280a3000520908d0860310d4cb8b400000c8780001217400000006"),
		 InlineData((ushort)0x000f, "642cd3d60000c3500802e5d4000f000000120000001708c5c80210071809280a3000520908d0860310d4cb8b400000c8780001217400000006")]
		// F2793 (hold-echo, seq 0x0004) / F2801 (hold-assert, seq 0x000f): the object-action STATE that holds the seated pose.
		public void BenchSitState_MatchesCapture(ushort seq, string expected)
		{
			var bytes = BehaviorControlCenterCodec.Build(BehaviorAnimations.BenchSitState(0x0802E5D4, seq, 0x12174));
			Assert.Equal(expected, Convert.ToHexStringLower(bytes));
		}

		// #428 the PlayAnimation (behaviour 0x07) relay - a jump the movement record cannot carry (mounted, or off a
		// bridge: a standing jump translates nowhere, so there is no CharDCMove to forward). Retail answers the client's
		// request with the SAME record at the ack seq, with the phase spliced in as f3 - which is precisely what the
		// generic relay builder produces, so the fix for the missing mounted jump was the relay GATE, not a new codec.
		//
		// Ground truth: NecroL80_mage-necromancer_login_falldamage_bridge-toast, c2s fr1318 -> s2c fr1321 (bodies below
		// with the 0x2000 carrier's u32 length prefix stripped). The args are f1=249527 (the animation-action id),
		// f2=2 (commandqueue_client_id, echoed back as the f3 phase), f5=10, f6=0, f11=65, f12=43.
		[Fact]
		public void PlayAnimationRelay_MatchesCapture()
		{
			var request = Convert.FromHexString("00000024642cd3d60000c3500802e5d40003000000070000000e08b79d0f1002280a30005841602b");
			const string expected = "642cd3d60000c3500802e5d40004000000070000001008b79d0f10021802280a30005841602b";

			var relay = BehaviorAcks.Relay(0x0802E5D4, request, 2);

			Assert.NotNull(relay);
			Assert.Equal(expected, Convert.ToHexStringLower(BehaviorControlCenterCodec.Build(relay)));
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
			var bytes = BehaviorControlCenterCodec.Build(BehaviorAnimations.PlayerSitOnObject(0x0802E5D4, 0x12174, rf));
			Assert.Equal(
				"642cd3d60000c3500802e5d400040000002a0000001408c4c802180a28083000520808f8900310f4c20400000002000e63686169725f7369745f63616c6d00000086402762770000000000aa3c72656d6f7465666f726d617420746f6b656e3d2273746172746f626a656374616374696f6e283333297c7475726e6f6e5f657865637574652834297c54617665726e42656e636853696d706c6544796e656c222063617465676f72793d22353030323622206b65793d2251217373405f332c76262333363b2c3f2c26616d703b47456d2671756f743b5b5f22206b6e75626f743d22302220203e3c2f72656d6f7465666f726d61743e0000004c",
				Convert.ToHexStringLower(bytes));
		}

		[Fact]
		public void PlayerStandUp_MatchesCapture()
		{
			// F2840: the stand-up animation ("standup") when the seated player moves.
			var rf = "<remoteformat id=\"0\" category=\"0\" key=\"C4O?^XE&#39;^VC4O?^XE&#39;^V\" knubot=\"0\"  ><parameter value=\"7;Standing up\" /></remoteformat>";
			var bytes = BehaviorControlCenterCodec.Build(BehaviorAnimations.PlayerStandUp(0x0802E5D4, 0x12174, rf));
			Assert.Equal(
				"642cd3d60000c3500802e5d400040000002a0000001408c5c802180c28083014520808f8900310f4c2040000000000077374616e64757000000000000000000000000000843c72656d6f7465666f726d61742069643d2230222063617465676f72793d223022206b65793d2243344f3f5e5845262333393b5e5643344f3f5e5845262333393b5e5622206b6e75626f743d22302220203e3c706172616d657465722076616c75653d22373b5374616e64696e6720757022202f3e3c2f72656d6f7465666f726d61743e00000001",
				Convert.ToHexStringLower(bytes));
		}

		// The remoteformat's length prefix must describe the string ACTUALLY written, for any string - not just the one
		// the capture happened to contain.
		//
		// This is the gate the two capture goldens above could not be: they feed the captured literal, so a hardcoded
		// prefix matches it by construction. In production the string is GENERATED (SitRemoteformat emits key="" until the
		// object's localized action key is imported), so it is a different length - and the baked 0xAA/0x84 prefixes then
		// overstated it by 33 and 25 bytes. The client read past the end of the record and discarded it whole
		// ("too short IIR UNKNOWN Key"), so sitting on a bench played nothing and the character stood straight back up.
		[Theory]
		[InlineData("")]
		[InlineData("TavernBenchSimpleDynel")]
		[InlineData("SomeMuchLongerObjectClassNameThatShiftsEveryFollowingOffset")]
		public void PlayerSitOnObject_LengthPrefixMatchesTheGeneratedRemoteformat(string objectClass)
		{
			var rf = BehaviorAnimations.SitRemoteformat(objectClass);

			AssertRemoteformatLengthPrefix(BehaviorControlCenterCodec.Build(BehaviorAnimations.PlayerSitOnObject(0x0802E5D4, 0x12174, rf)), rf);
		}

		[Fact]
		public void PlayerStandUp_LengthPrefixMatchesTheGeneratedRemoteformat()
		{
			var rf = BehaviorAnimations.StandRemoteformat();

			AssertRemoteformatLengthPrefix(BehaviorControlCenterCodec.Build(BehaviorAnimations.PlayerStandUp(0x0802E5D4, 0x12174, rf)), rf);
		}

		// Finds the remoteformat inside the built record and checks the u16 immediately before it equals its length. Also
		// asserts the string reaches the record's trailer, i.e. nothing was truncated.
		private static void AssertRemoteformatLengthPrefix(byte[] record, string remoteformat)
		{
			var expected = Encoding.Latin1.GetBytes(remoteformat);
			var at = record.AsSpan().IndexOf(expected);
			Assert.True(at > 2, $"the remoteformat is not in the record at all (looked for {expected.Length} bytes)");

			var declared = record.ReadU16Be(at - 2);
			Assert.Equal(expected.Length, declared);
			// ...and the record really does carry that many bytes after the prefix (the trailer is 4 bytes).
			Assert.Equal(record.Length, at + expected.Length + 4);
		}


		[Theory, InlineData(false, "642cd3d60000c3500802e5d40004000000040000001508dfb00b180528083000520908d0860310d4cb8b400000000100396ae90100000000000000000000000000"),
		 InlineData(true, "642cd3d60000c3500802e5d4000a000000040000001508dfb00b180528083000520908d0860310d4cb8b400000000100396ae90100000000000000000000000001")]
		// frame#1657 begin
		// frame#1658 complete
		public void SpellweaveComponentAdd_MatchesCapture(bool complete, string expected)
		{
			// the add gesture for element[0] = Arcane Renewal 0x396AE9.
			var bytes = BehaviorControlCenterCodec.Build(BehaviorAnimations.SpellweaveComponentAdd(0x0802E5D4, 0x396AE9, complete));
			Assert.Equal(expected, Convert.ToHexStringLower(bytes));
		}

		[Fact]
		public void SpellweaveTranceExit_MatchesCapture()
		{
			// cancel-weave capture, frame 141.61: the caster's trance-exit animation on cancel/fire.
			var bytes = BehaviorControlCenterCodec.Build(BehaviorAnimations.SpellweaveTranceExit(0x0802E5D4));
			Assert.Equal("642cd3d60000c3500802e5d40004000000010000000c08d6c202180628083000584f0000000400000027000000000000000000000000", Convert.ToHexStringLower(bytes));
		}

		[Fact]
		public void SpellweaveTrance_MatchesCapture()
		{
			// frame#1645: the caster's trance-pose animation on activate.
			var bytes = BehaviorControlCenterCodec.Build(BehaviorAnimations.SpellweaveTrance(0x0802E5D4));
			Assert.Equal("642cd3d60000c3500802e5d40004000000010000000d08b9c80218042808300058af090000002700000001000000000000000000000000", Convert.ToHexStringLower(bytes));
		}
		// #827: the one-stat update retail sends at Casilda's rescue, immediately before the three BCC records
		// (Hoxxling_mage-heraldofxotli_login_gameplay frame 1834, 25 bytes past the carrier length).
		//
		// ⭐ This is the record that actually lets her WALK. `CharacterStatId.CombatMode` (0x14) is "BCC State" to the
		// client and selects the behaviour tree; her type ships 80 - her captive tree - where 1,186 templates carry the
		// ordinary 1. Until it flips, her locomotion tree never runs, so the server moved her while she played a
		// standing animation. That is what "she slides / teleports" was, and no amount of getting the MoveTask right
		// fixed it: the movement was always arriving, the behaviour to animate it was not.
		[Fact]
		public void CasildasRescueBccState_MatchesCapture()
		{
			var bytes = StatUpdateCodec.Build(0x00017ED0, new CharacterStatBlock().SetRaw(CharacterStatId.CombatMode, 1));

			Assert.Equal("96b8dc590000c35000017ed000000000010000001400000001", Convert.ToHexString(bytes).ToLowerInvariant());
		}

		// #827: the slot-9 LOCOMOTION registration retail installs at the rescue - byte-exact against
		// Hoxxling_mage-heraldofxotli_login_gameplay idx 2106 (frame 1840), past the carrier's u32 length.
		//
		// ⚠️ We sent the SHORT bound form here for three rounds of fixes while she slid through a standing animation.
		// The movement packets were retail-exact and arriving the whole time; this is the behaviour that animates
		// them, and it was being registered with a tail truncated to its first 20 of 88 bytes.
		[Fact]
		public void CasildasFreedLocomotion_MatchesCapture()
		{
			var bytes = BehaviorControlCenterCodec.Build(BehaviorAnimations.CompanionLocomotion(0x00017ED0, 9, 42010, 7, 24, 0));

			Assert.Equal(
				"642cd3d60000c35000017ed00004000000090000000a089ac8021807281830000000001200000000000000000000000000000000" +
				"0000000000000000b290805c29e627cab290805c29e627cab290805c29e627cab290805c29e627cab290805c29e627cab290805c29e627ca" +
				"000000010000000100000001",
				Convert.ToHexString(bytes).ToLowerInvariant());
		}

	}
}
