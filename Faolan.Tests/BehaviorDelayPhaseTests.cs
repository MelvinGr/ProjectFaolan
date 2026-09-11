using System;
using Faolan.Core.Network.Codecs.Combat;
using Xunit;

namespace Faolan.Tests
{
	// Phase 0x0E of the BehaviorControlCenterIIR carries an extra u32 in front of the behaviorId - the delay word
	// `ReadSubClass` (FUN_005e24e0) reads into `this+0x24` and the record's consumer scales by 0.01 before adding it
	// to the running Command's timeline. Ten of the 67 299 captured BCC records are that shape, and reading
	// them without it made the delay look like the behaviorId: they showed up as commands 0x32/0x35/0x39/0x3A/0x47,
	// none of which the client's classmap registers. Read with the delay word split off, all ten are Magic:Attack
	// (0x04) or ObjectInteraction (0x2A) with a tail their already-decoded grammars fit exactly.
	//
	// The two records here are the shortest of each kind, from `loot-drag-1538` (frame 920) and
	// `Hoxxling_mage-heraldofxotli_login_gameplay` (frame 2412); `--report bcc-census --report-arg 0E` prints all ten.
	public class BehaviorDelayPhaseTests
	{
		// A cast at delay 50 -> 0.50 s. The tail is Magic:Attack's 22-byte `f == 1` form.
		private const string MagicAttackDelayRecord =
			"642cd3d60000c35001317608000e00000032000000040000001908f2a30510161810280a3000520808d0860310cfc80d60c801" +
			"0000000100125d9b010000000000000000000000c800";

		// A treasure chest at delay 57 -> 0.57 s. The tail is ObjectInteraction's 185-byte form (a `lootbag_open`
		// action name plus its remoteformat), a length the corpus already showed on the ordinary phases.
		private const string ObjectInteractionDelayRecord =
			"642cd3d60000c35001316e71000e000000390000002a0000001308c5c802183528083000520708fa9003108744" +
			"00000002000c6c6f6f746261675f6f70656e0000000040b333330000000000953c72656d6f7465666f726d617420746f6b656e" +
			"3d2273746172746f626a656374616374696f6e283138297c6f6e7573652832297c5472656173757265436865737453637269707422" +
			"2063617465676f72793d22353030323622206b65793d226c5a37402c355e44692347262339323b3975546039536a43222" +
			"06b6e75626f743d22302220203e3c2f72656d6f7465666f726d61743e00000001";

		// The ordinary phase, for contrast: another Magic:Attack, phase 0x0A, with no delay word - the behaviorId sits
		// one word earlier. `NecroL80_mage-necromancer_login_gameplay_chat-annotated` frame 13771.
		private const string PlainPhaseRecord =
			"642cd3d60000c3500002de7c000a000000040000001408db8904180128183000520808d0860310fcbc0b" +
			"0000002d003cadb90100000000000000000000000001";

		[Fact]
		public void SplitsTheDelayWordOffAMagicAttackCast()
		{
			var bcc = BehaviorControlCenterCodec.Parse(Convert.FromHexString(MagicAttackDelayRecord));

			Assert.Equal(0x000Eu, bcc.Seq);
			Assert.Equal(50u, bcc.DelayCentiseconds);
			Assert.Equal(0x00000004u, bcc.BehaviorId); // Magic:Attack, not the 0x32 the delay word used to read as
			Assert.Equal(25, bcc.Args.Length);
			Assert.Equal(22, bcc.Tail.Length);
		}

		[Fact]
		public void SplitsTheDelayWordOffAnObjectInteraction()
		{
			var bcc = BehaviorControlCenterCodec.Parse(Convert.FromHexString(ObjectInteractionDelayRecord));

			Assert.Equal(0x000Eu, bcc.Seq);
			Assert.Equal(57u, bcc.DelayCentiseconds);
			Assert.Equal(0x0000002Au, bcc.BehaviorId); // ObjectInteraction, not 0x39
			Assert.Equal(19, bcc.Args.Length);
			Assert.Equal(185, bcc.Tail.Length);
		}

		[Theory]
		[InlineData(MagicAttackDelayRecord)]
		[InlineData(ObjectInteractionDelayRecord)]
		[InlineData(PlainPhaseRecord)]
		public void RoundTripsTheCapturedBytes(string hex)
		{
			var record = Convert.FromHexString(hex);

			Assert.Equal(hex, Convert.ToHexStringLower(BehaviorControlCenterCodec.Build(BehaviorControlCenterCodec.Parse(record))));
		}

		[Fact]
		public void EveryOtherPhaseCarriesNoDelayWord()
		{
			var bcc = BehaviorControlCenterCodec.Parse(Convert.FromHexString(PlainPhaseRecord));

			Assert.Equal(0x000Au, bcc.Seq);
			Assert.Null(bcc.DelayCentiseconds);
			Assert.Equal(0x00000004u, bcc.BehaviorId);
		}

		// The phase byte is what puts the word on the wire, so a value that disagrees with it would build a body the
		// client reads one field out of step - exactly the failure this whole ticket was.
		[Fact]
		public void RefusesToBuildAValueWhosePhaseAndDelayDisagree()
		{
			var withoutDelay = BehaviorControlCenterCodec.Parse(Convert.FromHexString(MagicAttackDelayRecord)) with { DelayCentiseconds = null };
			var withDelay = BehaviorControlCenterCodec.Parse(Convert.FromHexString(PlainPhaseRecord)) with { DelayCentiseconds = 50 };

			Assert.Throws<ArgumentException>(() => BehaviorControlCenterCodec.Build(withoutDelay));
			Assert.Throws<ArgumentException>(() => BehaviorControlCenterCodec.Build(withDelay));
		}
	}
}
