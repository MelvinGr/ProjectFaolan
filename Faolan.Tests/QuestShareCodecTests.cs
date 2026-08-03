using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Core.Network.Constants;
using Xunit;

namespace Faolan.Tests
{
	// The three quest-share dispatch bodies (#101). No capture performs a share, so unlike every other codec these
	// cannot be gated against retail bytes - what CAN be gated is that each one matches the client's own READER, field
	// for field, which is the only evidence there is:
	//
	//   FUN_004c5bf0  DispatchQuestShareRequest            u32, u32 (one ID32), u32, string
	//   FUN_004c58f0  DispatchQuestShareGlobalFeedback     u32, u32
	//   FUN_004c5a50  DispatchQuestShareIndividualFeedback u32, string
	//
	// So these tests assert the WIDTHS and ORDER by hand rather than round-tripping a fixture: a byte count and an
	// explicit hex string is exactly the claim the client's reader makes, and it is the claim that breaks if someone
	// "fixes" i_cFeedbackValue into an ID32 (it is a string - the reader calls the string primitive).
	public class QuestShareCodecTests
	{
		[Fact]
		public void ShareRequest_IsAnId32ThenTheTemplateThenTheName()
		{
			var body = QuestShareRequestCodec.Build(0x01000001, 13479, "The Scavenger");

			// [0000c350][01000001] [000034a7] [000d "The Scavenger"] - 8 + 4 + 2 + 13.
			Assert.Equal("0000c35001000001000034a7000d5468652053636176656e676572", Hex(body));
			Assert.Equal(27, body.Length);
		}

		[Fact]
		public void GlobalFeedback_IsTwoU32()
		{
			var body = QuestShareGlobalFeedbackCodec.Build(QuestShareFeedback.LegitTargets, 3);

			Assert.Equal("0000000500000003", Hex(body));
		}

		[Fact]
		public void IndividualFeedback_CarriesTheNameAsAString_NotAnId32()
		{
			var body = QuestShareIndividualFeedbackCodec.Build(QuestShareFeedback.QuestDeclined, "Conan");

			// [00000008][0005 "Conan"] - 4 + 2 + 5. An ID32 would be a fixed 8 bytes with no length prefix.
			Assert.Equal("000000080005436f6e616e", Hex(body));
		}

		[Theory]
		[InlineData(QuestShareFeedback.NotInTeam, 0u)]
		[InlineData(QuestShareFeedback.OutOfRangeTargets, 7u)]
		public void GlobalFeedback_RoundTrips(QuestShareFeedback type, uint value)
		{
			var parsed = QuestShareGlobalFeedbackCodec.Parse(QuestShareGlobalFeedbackCodec.Build(type, value));

			Assert.Equal(type, parsed.Type);
			Assert.Equal(value, parsed.Value);
		}

		[Fact]
		public void IndividualFeedback_RoundTrips()
		{
			var parsed = QuestShareIndividualFeedbackCodec.Parse(QuestShareIndividualFeedbackCodec.Build(QuestShareFeedback.PlayerBusy, "Kalanthes"));

			Assert.Equal(QuestShareFeedback.PlayerBusy, parsed.Type);
			Assert.Equal("Kalanthes", parsed.PlayerName);
		}

		[Fact]
		public void ShareRequest_RoundTrips()
		{
			var parsed = QuestShareRequestCodec.Parse(QuestShareRequestCodec.Build(0x01000001, 13479, "The Scavenger"));

			Assert.Equal(EntityClass.Character, parsed.Sharer.Class);
			Assert.Equal(0x01000001u, parsed.Sharer.Id);
			Assert.Equal(13479u, parsed.StartTaskTemplate);
			Assert.Equal("The Scavenger", parsed.QuestName);
		}

		// Parse is strict: a trailing byte is a body we cannot account for, not something to ignore.
		[Fact]
		public void AnOverlongBody_Throws()
		{
			var body = QuestShareGlobalFeedbackCodec.Build(QuestShareFeedback.NoLegitTargets);

			Assert.Throws<CodecParseException>(() => QuestShareGlobalFeedbackCodec.Parse([.. body, 0x00]));
		}

		private static string Hex(byte[] bytes)
		{
			return Convert.ToHexString(bytes).ToLowerInvariant();
		}
	}
}
