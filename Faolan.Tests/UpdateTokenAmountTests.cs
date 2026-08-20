using System;
using System.Linq;
using Faolan.Core.Network.Codecs.Economy;
using Xunit;

namespace Faolan.Tests
{
	// Golden-byte tests for DispatchUpdateTokenAmount (s2c 0x204D) against every frame the corpus carries - 10
	// frames over 10 captures, all 18 bytes, listed by `--report method-bodies --report-arg UpdateTokenAmount`.
	// The four distinct bodies below ARE that set: five character-creation captures send the first one verbatim.
	public class UpdateTokenAmountTests
	{
		// (hex, decoded) - the four distinct captured bodies and the event each was sent on.
		public static TheoryData<string, byte, ulong> CapturedFrames() => new()
		{
			// five *-create captures, at first world entry, right after N3Teleport
			{ "010000000000000000000000000000000000", 1, 0 },
			// xxx_mage-necromancer_login_play3 - completing Destiny quest 10915 "Nadini"
			{ "040000000000000001000000000000000000", 4, 1 },
			// xxx_mage-necromancer_login_play4 - same character, completing quest 10830 "Rescue Arias"
			{ "040000000000000002000000000000000000", 4, 2 },
			// NecroL80_..._group-finder
			{ "200000000000000002000000000000000000", 0x20, 2 },
			// Hoxxling_..._login_claim_login_reward - beside CoreClaimIIR "login counter 90"
			{ "320000000000000003000000000000000000", 0x32, 3 }
		};

		[Theory]
		[MemberData(nameof(CapturedFrames))]
		public void UpdateTokenAmount_RoundTripsEveryCapturedBody(string hex, byte tokenId, ulong gained)
		{
			var captured = Convert.FromHexString(hex);
			var parsed = UpdateTokenAmountCodec.Parse(captured);

			Assert.Equal(tokenId, parsed.TokenId);
			Assert.Equal(gained, parsed.Gained);
			// `spent` and `includeBonus` are 0 in every captured frame - the corpus pins their POSITION, not a value.
			Assert.Equal(0UL, parsed.Spent);
			Assert.False(parsed.IncludeBonus);

			Assert.Equal(hex, Convert.ToHexStringLower(UpdateTokenAmountCodec.Build(parsed)));
		}

		[Fact]
		public void UpdateTokenAmount_WritesTheTailFieldsTheCorpusNeverExercises()
		{
			// Nothing in the corpus sets `spent` or `includeBonus`, so this is the only thing holding their offsets:
			// spent at 9..16 big-endian, the flag as the last byte.
			var body = UpdateTokenAmountCodec.Build(new UpdateTokenAmount(0x32, 0x0102030405060708, 0x1112131415161718, true));

			Assert.Equal("32010203040506070811121314151617" + "1801", Convert.ToHexStringLower(body));
		}

		[Fact]
		public void UpdateTokenAmount_RejectsABodyThatIsNot18Bytes()
		{
			Assert.ThrowsAny<Exception>(() => UpdateTokenAmountCodec.Parse(Convert.FromHexString("0100000000000000000000000000000000")));
			Assert.ThrowsAny<Exception>(() => UpdateTokenAmountCodec.Parse(Convert.FromHexString("01000000000000000000000000000000000000")));
		}
	}
}
