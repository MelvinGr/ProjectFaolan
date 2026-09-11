using System;
using Faolan.Core.Network.Codecs.Combat;
using Xunit;

namespace Faolan.Tests
{
	// Byte-exact gate for the 0x2018 DispatchResurrectRequest body - the resurrect offer.
	//
	// The oracle is the only one that exists: `NecroL80_..._annotated_9` frame 6467, the instant the player used a
	// Self-Resurrection Potion. One packet in a 137-capture corpus, so every field name below is the client's rather
	// than a reading - its dispatcher asserts "stream bad after 'i_cResurrectorID' / 'i_nSpellTemplate' /
	// 'i_castSpellOnResurrect'" as it reads them in that order.
	public class ResurrectRequestCodecTests
	{
		// [ID32 Character 0x0802E5D4 = the player themselves][u32 0x0049DDF7 spell][u8 01 cast on resurrect]
		private const string CapturedBody = "0000c3500802e5d40049ddf701";

		[Fact]
		public void CapturedBody_Decodes()
		{
			var value = ResurrectRequestCodec.Parse(Convert.FromHexString(CapturedBody));

			// A self-resurrection: the resurrector is the player, which is what the potion is for.
			Assert.Equal(0x0802E5D4u, value.ResurrectorId);
			Assert.Equal(0x0049DDF7u, value.SpellTemplate);
			Assert.True(value.CastSpellOnResurrect);
		}

		[Fact]
		public void CapturedBody_RoundTrips()
		{
			var captured = Convert.FromHexString(CapturedBody);

			Assert.Equal(captured, ResurrectRequestCodec.Build(ResurrectRequestCodec.Parse(captured)));
		}

		// Our own offer is built from the character and the spell its consumable arms, and it has to produce the
		// captured shape - only the two ids differ from retail's, because they are that session's.
		[Fact]
		public void BuiltFromColumns_HasTheCapturedShape()
		{
			var body = ResurrectRequestCodec.Build(0x0802E5D4u, 0x0049DDF7u);

			Assert.Equal(CapturedBody, Convert.ToHexString(body).ToLowerInvariant());
		}

		[Fact]
		public void CastSpellOnResurrect_IsTheTrailingByte()
		{
			Assert.EndsWith("00", Convert.ToHexString(ResurrectRequestCodec.Build(1, 2, false)).ToLowerInvariant());
			Assert.False(ResurrectRequestCodec.Parse(ResurrectRequestCodec.Build(1, 2, false)).CastSpellOnResurrect);
		}

		[Fact]
		public void Parse_ThrowsOnAWrongEntityClass()
		{
			// 0xC351 instead of the Character class word - a body that is not this message.
			Assert.ThrowsAny<Exception>(() => ResurrectRequestCodec.Parse(Convert.FromHexString("0000c3510802e5d40049ddf701")));
		}

		[Theory]
		[InlineData("0000c3500802e5d40049ddf7")] // no flag
		[InlineData("0000c3500802e5d40049ddf70100")] // one byte too many
		public void Parse_IsStrictAboutTheLength(string hex)
		{
			Assert.ThrowsAny<Exception>(() => ResurrectRequestCodec.Parse(Convert.FromHexString(hex)));
		}
	}
}
