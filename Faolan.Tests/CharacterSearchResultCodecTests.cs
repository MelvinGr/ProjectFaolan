using System;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Team;
using Xunit;

namespace Faolan.Tests
{
	// Byte-exact gate for the 0x2010 DispatchCharacterSearchResult body.
	//
	// The oracle is a retail body carrying three characters, which is what made this message worth decoding: the "who"
	// result reuses TeamMemberRecord verbatim, and three characters at three different levels identified the field the
	// team captures could not - both of their members were level 5, so the word sat in the codec as a constant.
	public class CharacterSearchResultCodecTests
	{
		// Hoxxling_mage-heraldofxotli_login: the answer to a CharacterSearch for levels 5..10. Ryuuen (level 8),
		// Coombos (7) and Revicta (7) - every one inside the requested range, which is the confirmation that the word
		// is a level and not another per-character opaque.
		private const string CapturedBody =
			"00000fc4" +
			"0000c35001316d83000652797575656e000000220000000800000fb4000000010000000000000006000000000000016200" +
			"00c79c00000fb40000000000000000000000000000000100009c5001eab81f000000000159ea570c00000000000000000000" +
			"0004" +
			"0000c35001316e6e0007436f6f6d626f73000000160000000700000fb4000000020000000000000006000000000000016200" +
			"00c79c00000fb40000000000000000000000000000000100009c5001eab81f000000030159ea5efb00000000000000000000" +
			"0004" +
			"0000c35001316fb90007526576696374610000001c0000000700000fb6000000030000000000000006000000000000016200" +
			"00c79c00000fb6000000026b59d6b1000000000000000d00009c5001eabe5700000180013bbc1f890000000000000000" +
			"00000004" +
			"00";

		private static byte[] Captured()
		{
			return Convert.FromHexString(CapturedBody);
		}

		[Fact]
		public void Decode_ReadsTheThreeCapturedCharacters()
		{
			var result = CharacterSearchResultCodec.Parse(Captured());

			Assert.Equal(3, result.Characters.Count);

			Assert.Equal("Ryuuen", result.Characters[0].Name);
			Assert.Equal(8u, result.Characters[0].Level);
			Assert.Equal(4020u, result.Characters[0].MapId);

			Assert.Equal("Coombos", result.Characters[1].Name);
			Assert.Equal(7u, result.Characters[1].Level);

			Assert.Equal("Revicta", result.Characters[2].Name);
			Assert.Equal(7u, result.Characters[2].Level);
			Assert.Equal(4022u, result.Characters[2].MapId); // a different playfield from the other two
		}

		// The point of the level decode: every returned character is inside the 5..10 the client asked for. A word
		// that were anything else would have no reason to be.
		[Fact]
		public void EveryReturnedLevel_IsInsideTheRequestedRange()
		{
			foreach (var c in CharacterSearchResultCodec.Parse(Captured()).Characters)
				Assert.InRange(c.Level, 5u, 10u);
		}

		[Fact]
		public void DecodeThenEncode_ReproducesTheCapturedBody_ByteExact()
		{
			var captured = Captured();

			Assert.Equal(captured, CharacterSearchResultCodec.Build(CharacterSearchResultCodec.Parse(captured)));
		}

		// The trailing byte is i_bMoreHits, not a terminator. It is 0 in every captured body - each captured
		// search fitted in one page - so the two readings agree on the corpus and only differ on a truncated page,
		// which is exactly the case the flag exists for.
		[Fact]
		public void TheTrailingByte_IsTheMoreHitsFlag()
		{
			Assert.False(CharacterSearchResultCodec.Parse(Captured()).MoreHits);

			var truncated = CharacterSearchResultCodec.Build(CharacterSearchResultCodec.Parse(Captured()) with { MoreHits = true });

			Assert.Equal((byte)0x01, truncated[^1]);
			Assert.True(CharacterSearchResultCodec.Parse(truncated).MoreHits);
			Assert.Equal(3, CharacterSearchResultCodec.Parse(truncated).Characters.Count); // and nothing else moved
		}

		// An empty result still carries the frame and the trailer - dropping the trailer makes the client run off the
		// end of the body and drop the whole CSPlayerAgent route.
		[Fact]
		public void EmptyResult_KeepsTheFrameAndTheTrailer()
		{
			var body = CharacterSearchResultCodec.Build(new CharacterSearchResult([]));

			Assert.Equal(new byte[] { 0x00, 0x00, 0x03, 0xF1, 0x00 }, body);
		}

		[Fact]
		public void BuiltFromColumns_RoundTrips()
		{
			var built = new CharacterSearchResult([
				new TeamMemberRecord
				{
					CharacterId = 0x0131FFFF,
					Name = "Faolan",
					Class = ConanClass.Necromancer,
					Level = 42,
					MapId = 4010
				}
			]);

			var round = CharacterSearchResultCodec.Parse(CharacterSearchResultCodec.Build(built));

			Assert.Single(round.Characters);
			Assert.Equal("Faolan", round.Characters[0].Name);
			Assert.Equal(42u, round.Characters[0].Level);
			Assert.Equal(ConanClass.Necromancer, round.Characters[0].Class);
		}
	}
}
