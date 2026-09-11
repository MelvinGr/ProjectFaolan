using System;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Combat;
using Xunit;

namespace Faolan.Tests
{
	// The two IIRs a timeline needs that had no codec: which spell was applied, and what the player had selected.
	// Bytes are lifted verbatim from xxx_mage-necromancer_login_play2_tortage_city_first-quests, with the u32 length
	// prefix stripped - codecs take the IIR content.
	public class MagicBuffLookAtCodecTests
	{
		// Short-form apply: the pet buffing the player with Fatality (5070797 = 0x4D5FCD).
		private const string ShortBuff = "6b87e3160000c35001317cf800004d5fcd010000c35001317cf8";

		// Long form (84 B on the wire): opens identically and echoes the spell id later, which is what pins offset 13.
		private const string LongBuff =
			"6b87e3160000c35001317cf80000119122000000c35001317cf80000000000000000001446e900010000000100000000001191220000000500000000000000000000000000000005000000000b9aeb25";

		[Fact]
		public void MagicBuff_ParsesShortForm()
		{
			var buff = MagicBuffCodec.Parse(Convert.FromHexString(ShortBuff));
			Assert.Equal(0x01317CF8u, buff.ApplyTo);
			Assert.Equal(0x01317CF8u, buff.ApplyFrom);
			Assert.Equal(5070797u, buff.SpellBaseId); // Fatality
			Assert.Equal(MagicBuffIds.FormEnd, buff.Form); // the low byte of the wire id, the client's form selector
			Assert.Empty(buff.Data);
		}

		// The long form's second copy of the id is the independent check on the offset: read it wrong and the two
		// disagree. 0x119122 = 1151266 = Runed Flesh (Rank 1).
		[Fact]
		public void MagicBuff_LongFormSpellIdIsEchoed()
		{
			var body = Convert.FromHexString(LongBuff);
			var buff = MagicBuffCodec.Parse(body);
			Assert.Equal(1151266u, buff.SpellBaseId);
			Assert.Contains("00119122", LongBuff[26..], StringComparison.Ordinal);
			// The whole record is modelled, so the 54-byte data region round-trips too.
			Assert.Equal(MagicBuffDataCodec.Length, buff.Data.Length);
			Assert.Equal(body, MagicBuffCodec.Build(buff));
		}

		[Fact]
		public void MagicBuff_RoundTripsBuild()
		{
			var body = MagicBuffCodec.Build(0x01317CF8, 0x01317CF8, MagicBuffIds.WireIdOf(5070797, MagicBuffIds.FormEnd));
			Assert.Equal(ShortBuff, Convert.ToHexString(body).ToLowerInvariant());
		}

		[Fact]
		public void MagicBuff_RejectsAnotherIir()
		{
			var notAMagicBuff = Convert.FromHexString("0d1556950000c35001317cf8000000c3500002792900000000");
			Assert.Throws<CodecParseException>(() => MagicBuffCodec.Parse(notAMagicBuff));
		}

		// Selecting Turach, then clearing the selection. A zero target is a deselect, not an absent field.
		[Fact]
		public void LookAt_ParsesSelectAndDeselect()
		{
			var select = LookAtCodec.Parse(Convert.FromHexString("0d1556950000c35001317cf8000000c350000279290000000000000002"));
			Assert.Equal(0x01317CF8u, select.Subject);
			Assert.Equal(0x00027929u, select.Target); // Turach

			// A cleared selection: Both target words are zeroed rather than the field being absent.
			var cleared = LookAtCodec.Parse(LookAtCodec.Build(0x01317CF8, 0));
			Assert.Equal(0u, cleared.Target);
			Assert.Equal(0u, cleared.TargetClass);
		}

		[Fact]
		public void LookAt_RoundTripsBuild()
		{
			Assert.Equal("0d1556950000c35001317cf8000000c350000279290000000000000002",
				Convert.ToHexString(LookAtCodec.Build(0x01317CF8, 0x00027929)).ToLowerInvariant());
		}
	}
}
