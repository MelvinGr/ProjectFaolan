using System;
using Faolan.Core.Network.Codecs.Combat;
using Xunit;

namespace Faolan.Tests
{
	// The two IIRs a timeline needs that had no codec: which spell was applied, and what the player had selected.
	// Bytes are lifted verbatim from xxx_mage-necromancer_login_play2_tortage_city_first-quests, with the u32 length
	// prefix stripped - codecs take the IIR CONTENT.
	public class MagicBuffLookAtCodecTests
	{
		// Short-form apply: the pet buffing the player with Fatality (5070797 = 0x4D5FCD).
		private const string ShortBuff = "6b87e3160000c35001317cf800004d5fcd010000c35001317cf8";

		// Long form (84 B on the wire): opens identically and ECHOES the spell id later, which is what pins offset 13.
		private const string LongBuff =
			"6b87e3160000c35001317cf80000119122000000c35001317cf80000000000000000001446e900010000000100000000001191220000000500000000000000000000000000000005000000000b9aeb25";

		[Fact]
		public void MagicBuff_ParsesShortForm()
		{
			Assert.True(MagicBuffCodec.TryParse(Convert.FromHexString(ShortBuff), out var caster, out var spell, out var target));
			Assert.Equal(0x01317CF8u, caster);
			Assert.Equal(5070797u, spell); // Fatality
			Assert.Equal(0x01317CF8u, target);
		}

		// The long form's second copy of the id is the independent check on the offset: read it wrong and the two
		// disagree. 0x119122 = 1151266 = Runed Flesh (Rank 1).
		[Fact]
		public void MagicBuff_LongFormSpellIdIsEchoed()
		{
			var body = Convert.FromHexString(LongBuff);
			Assert.True(MagicBuffCodec.TryParse(body, out _, out var spell, out _));
			Assert.Equal(1151266u, spell);
			Assert.Contains("00119122", LongBuff[26..], StringComparison.Ordinal);
		}

		[Fact]
		public void MagicBuff_RoundTripsBuild()
		{
			var body = MagicBuffCodec.Build(0x01317CF8, 5070797, 0x01317CF8);
			Assert.Equal(ShortBuff, Convert.ToHexString(body).ToLowerInvariant());
		}

		[Fact]
		public void MagicBuff_RejectsAnotherIir()
		{
			Assert.False(MagicBuffCodec.TryParse(Convert.FromHexString("0d1556950000c35001317cf8000000c3500002792900000000"), out _, out _, out _));
		}

		// Selecting Turach, then clearing the selection. A zero target is a DESELECT, not an absent field.
		[Fact]
		public void LookAt_ParsesSelectAndDeselect()
		{
			Assert.True(LookAtCodec.TryParse(
				Convert.FromHexString("0d1556950000c35001317cf8000000c350000279290000000000000002"), out var subject, out var target));
			Assert.Equal(0x01317CF8u, subject);
			Assert.Equal(0x00027929u, target); // Turach

			// A cleared selection: the target endpoint is zeroed rather than the field being absent.
			Assert.True(LookAtCodec.TryParse(LookAtCodec.Build(0x01317CF8, 0), out _, out var cleared));
			Assert.Equal(0u, cleared);
		}

		[Fact]
		public void LookAt_RoundTripsBuild()
		{
			Assert.Equal("0d1556950000c35001317cf8000000c350000279290000000000000002",
				Convert.ToHexString(LookAtCodec.Build(0x01317CF8, 0x00027929)).ToLowerInvariant());
		}
	}
}
