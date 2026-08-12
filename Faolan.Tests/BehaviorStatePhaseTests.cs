using System;
using Faolan.Core.Network.Codecs.Combat;
using Xunit;

namespace Faolan.Tests
{
	// Phases 0x0B and 0x0C of the BehaviorControlCenterIIR carry a bare u32 INSTEAD of a command (#809). The client's
	// `ReadSubClass` (FUN_005e24e0) gives them their own switch arm - read one word into `this+0x28`, return - and its
	// writer (FUN_005e2650) mirrors it, so the record ends there and is 18 bytes on the wire. The codec used to read
	// every phase as a command and so refused all nine captured records, running out of bytes looking for an argsLen.
	//
	// What the word MEANS is undecoded [V0]: all nine are s2c, so no client-side call site names it. It is 60 on four
	// records from four different actors and captures, 84 on three from one NPC and 33 / 83 once each.
	public class BehaviorStatePhaseTests
	{
		// Hoxxling_mage-heraldofxotli_login_gameplay2 frame 5461 - phase 0x0C, value 60.
		private const string StatePhase0CRecord = "642cd3d60000c35001316dc2000c0000003c";

		// NecroL80_mage-necromancer_login_gameplay_annotated frame 7304 - phase 0x0B, value 33.
		private const string StatePhase0BRecord = "642cd3d60000c350021667cc000b00000021";

		[Theory]
		[InlineData(StatePhase0CRecord, 0x000C, 0x01316DC2u, 60u)]
		[InlineData(StatePhase0BRecord, 0x000B, 0x021667CCu, 33u)]
		public void ReadsTheSubstateIdAndEndsThere(string hex, int seq, uint actor, uint state)
		{
			var bcc = BehaviorControlCenterCodec.Parse(Convert.FromHexString(hex));

			Assert.Equal((ushort)seq, bcc.Seq);
			Assert.Equal(actor, bcc.Actor);
			Assert.Equal(state, bcc.SubstateId);

			// No command block at all - not an empty one.
			Assert.Equal(0u, bcc.BehaviorId);
			Assert.Empty(bcc.Args);
			Assert.Empty(bcc.Tail);

			Assert.Equal(hex, Convert.ToHexStringLower(BehaviorControlCenterCodec.Build(bcc)));
		}

		// The phase byte is what says which shape a record is, so a value that disagrees with it would build a body
		// the client reads out of step - the same guard the delay word already had.
		[Fact]
		public void RefusesASubstateIdOnACommandPhase()
		{
			Assert.Throws<ArgumentException>(() => BehaviorControlCenterCodec.Build(new BehaviorControlCenterIIR(0x021667CC, 0x0004, 0, [], [], SubstateId: 33)));
		}

		[Fact]
		public void RefusesACommandOnAStatePhase()
		{
			Assert.Throws<ArgumentException>(() => BehaviorControlCenterCodec.Build(new BehaviorControlCenterIIR(0x021667CC, 0x000B, 0x0F, [], [], SubstateId: 33)));
		}

		// The client's `default:` arm reads nothing, so an unlisted phase ends at the seq word exactly like the
		// explicitly terminal 6/8/9/0xD/0x11 do. Reading it as a command would swallow whatever follows.
		[Fact]
		public void AnUnlistedPhaseEndsAtTheSeqWord()
		{
			var bcc = BehaviorControlCenterCodec.Parse(Convert.FromHexString("642cd3d60000c35001316dc20009"));

			Assert.Equal(0x0009, bcc.Seq);
			Assert.Null(bcc.SubstateId);
			Assert.Empty(bcc.Args);
		}
	}
}
