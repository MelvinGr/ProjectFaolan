using System;
using Faolan.Core.Network.Packets;
using Xunit;
using Faolan.Core.Network.Codecs;

namespace Faolan.Tests
{
	// Byte-exact gate for the NpcSpell effect-payload codec (#304): Parse(captured) then ToBytes() reproduces the
	// captured 54-byte ApplySpell effect payload for every field shape seen in the captures. Samples cover a normal
	// buff (Rend), a debuff (Rooted), the magnitude != rank case (Runed Flesh @29=14/@45=21) and a big-value cast.
	public class NpcSpellEffectCodecTests
	{
		[Theory, InlineData("0000000000000000000002BC000000000001000000000031644900000001000000000000000000000000000000010000000000541455"),
		 InlineData("00000000000000000000025800000000000100000000000337D200000004000000010000000000000000000000040000000004D44D7D"),
		 InlineData("000000000000000000000DAC00010000000100000000001191210000000E000000000000000000000000000000150000000000CACCE3"),
		 InlineData("0000000000000000000005DC000000000001000000000033A6C80000002A000000000000000000000000000000170000000014D3C4D4")]
		public void Codec_RoundTripsCapturedPayload(string hex)
		{
			var captured = Convert.FromHexString(hex);
			var effect = NpcSpellEffect.Parse(captured);

			Assert.Equal(hex, Convert.ToHexString(effect.ToBytes()));
		}

		[Fact]
		public void Parse_ReadsTheDecodedFields()
		{
			// Rend rank 1: duration 700 (0x2BC), spell id 0x31644900, magnitude/rank 1, flags 0/1/0, counter present.
			var effect = NpcSpellEffect.Parse(Convert.FromHexString("0000000000000000000002BC000000000001000000000031644900000001000000000000000000000000000000010000000000541455"));

			Assert.Equal(700, effect.Duration);
			Assert.Equal(0x31644900u, effect.SpellId);
			Assert.Equal(1, effect.Magnitude);
			Assert.Equal(1, effect.Rank);
			Assert.Equal(1, effect.FlagB);
		}
	}
}
