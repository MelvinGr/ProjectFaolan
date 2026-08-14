using System;
using System.Linq;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Quest;
using Xunit;

namespace Faolan.Tests
{
	// The pick-one-of-N quest reward exchange (#815) - two IIR types the opcode catalog could not even NAME before
	// this, so the whole subsystem read as raw hex in a timeline.
	//
	// Oracle: xxx_mage-necromancer_login_play3, quest 10928 "The Trainer of Mages" (Destiny 05 Mage). The maintainer
	// confirmed the in-client reading independently ("the quest reward had 3 possible options"), and the capture then
	// closes the loop by itself - the pick is answered with a DispatchAddItem for the template at that index.
	public class QuestRewardCodecTests
	{
		// Frame 2864: the offer, three options.
		private const string CapturedOffer =
			"8bb2799c0000c35001317cf80100002ab0000000000000000300006c0700006c07000000000000000500000000684e0be9beb853fe041347d7660cdf13684e0be9beb853fe041347d7660cdf13b290805c29e627cab290805c29e627ca00000001002ffe77002ffe770000000000000005000000001fb3f7475b6f12973dcb757f7fbc62b51fb3f7475b6f12973dcb757f7fbc62b5b290805c29e627cab290805c29e627ca000000010033cc770033cc770000000000000005000000004735febcdcb11519ded73456fac62d214735febcdcb11519ded73456fac62d21b290805c29e627cab290805c29e627ca000000010000000100000000000000000000000100000001";

		// Frame 2910: the pick. Frame 2911: the confirm, no options.
		private const string CapturedSelection = "80138d7b0000c35001317cf80100002ab000000002";

		private const string CapturedConfirm =
			"8bb2799c0000c35001317cf80100002ab000000000000000000000000000000000000000000000000200000000";

		[Fact]
		public void ParseOffer_ReadsTheThreeOptionsAndRoundTrips()
		{
			var captured = Convert.FromHexString(CapturedOffer);

			var parsed = QuestRewardNotifyCodec.Parse(captured);

			Assert.Equal(10928u, parsed.QuestId);
			Assert.Equal(3, parsed.Options.Count);

			// Acolyte's Dagger / Longsword / Staff - the three the client showed.
			Assert.Equal([27655u, 3145335u, 3394679u], parsed.Options.Select(o => o.ItemTemplateId));

			// The batch guid is one value shared by the whole response, the way a claim list's is.
			Assert.Single(parsed.Options.Select(o => Convert.ToHexStringLower(o.BatchGuid)).Distinct());

			Assert.All(parsed.Options, o => Assert.Equal(5ul, o.Level));
			Assert.Equal(Convert.ToHexStringLower(captured), Convert.ToHexStringLower(QuestRewardNotifyCodec.Build(parsed)));
		}

		// What pins the entry layout is not a plausible-looking alignment but the KEYS: each option's 16 bytes are the
		// item's real ProtoDecryptionKey, so they must equal the shipped Items rows exactly or the client would render
		// the option as "No name". A layout that were off by a field would not reproduce three of these.
		[Theory]
		[InlineData(0, 27655u, "684e0be9beb853fe041347d7660cdf13")] // Acolyte's Dagger
		[InlineData(1, 3145335u, "1fb3f7475b6f12973dcb757f7fbc62b5")] // Acolyte's Longsword
		[InlineData(2, 3394679u, "4735febcdcb11519ded73456fac62d21")] // Acolyte's Staff
		public void ParseOffer_EachOptionCarriesItsItemsDecryptionKey(int index, uint template, string key)
		{
			var option = QuestRewardNotifyCodec.Parse(Convert.FromHexString(CapturedOffer)).Options[index];

			Assert.Equal(template, option.ItemTemplateId);
			Assert.Equal(key, Convert.ToHexStringLower(option.DecryptionKey));
		}

		[Fact]
		public void ParseSelection_ReadsTheChosenIndexAndRoundTrips()
		{
			var captured = Convert.FromHexString(CapturedSelection);

			var parsed = QuestRewardSelectionCodec.Parse(captured);

			Assert.Equal(10928u, parsed.QuestId);
			Assert.Equal(2u, parsed.Index);
			Assert.Equal(Convert.ToHexStringLower(captured), Convert.ToHexStringLower(QuestRewardSelectionCodec.Build(parsed)));
		}

		// The index is 0-BASED: the capture's pick of 2 was answered with the THIRD option's template.
		[Fact]
		public void TheSelectedIndexAddressesTheOfferedListZeroBased()
		{
			var offer = QuestRewardNotifyCodec.Parse(Convert.FromHexString(CapturedOffer));
			var pick = QuestRewardSelectionCodec.Parse(Convert.FromHexString(CapturedSelection));

			// 3394679 = "Acolyte's Staff", the template the following DispatchAddItem granted.
			Assert.Equal(3394679u, offer.Options[(int)pick.Index].ItemTemplateId);
		}

		// The confirm is the SAME grammar with an empty option list - which is what makes one codec correct for both.
		[Fact]
		public void ParseConfirm_IsTheSameGrammarWithNoOptions()
		{
			var captured = Convert.FromHexString(CapturedConfirm);

			var parsed = QuestRewardNotifyCodec.Parse(captured);

			Assert.Equal(10928u, parsed.QuestId);
			Assert.Empty(parsed.Options);
			Assert.Equal(Convert.ToHexStringLower(captured), Convert.ToHexStringLower(QuestRewardNotifyCodec.Build(parsed)));
		}

		[Fact]
		public void Parse_RejectsATruncatedBody()
		{
			var truncated = Convert.FromHexString(CapturedOffer)[..40];

			Assert.Throws<CodecParseException>(() => QuestRewardNotifyCodec.Parse(truncated));
		}
	}
}
