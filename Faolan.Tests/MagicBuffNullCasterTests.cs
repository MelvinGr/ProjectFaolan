using System;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Combat;
using Xunit;

namespace Faolan.Tests
{
	// A MagicBuff can carry no caster: retail zeroes the class word where the applyFrom entity class would be, and the
	// instance behind it. 14 of the 4,666 MagicBuff bodies in the capture corpus do this, across three captures
	// (Hakotep_hox_login_gameplay0, NecroL80_mage-necromancer_login_combat, ..._chat-annotated-4).
	//
	// They are the same grammar rather than a misread, and the body length is what says so: all 4,666 bodies are either
	// 26 bytes (the END/short form) or 80 (the apply form), and the 14 sit in those same two buckets. A shifted or
	// mis-routed read could not land on exactly the two normal lengths.
	//
	// Requiring the class word here is what aborted a full capture import at the consumable-uses module.
	public class MagicBuffNullCasterTests
	{
		// The captured bytes, verbatim: an 80-byte apply and a 26-byte end, both with a zero applyFrom.
		private const string CapturedApply = "6B87E3160000C3501101B4F500004CBD2900000000000000000000000000000000000000003200000000000100000000";
		private const string CapturedEnd = "6B87E3160000C3500802E5D400004CBD29010000000000000000";

		[Theory, InlineData(CapturedApply), InlineData(CapturedEnd)]
		public void ANullCaster_ParsesAsNull_AndRebuildsByteExact(string hex)
		{
			var wire = Convert.FromHexString(hex);
			var parsed = MagicBuffCodec.Parse(wire);

			// Null, not 0: entity 0 is a real handle, so flattening the two together would attribute the buff to it.
			Assert.Null(parsed.ApplyFrom);
			Assert.NotEqual(0u, parsed.ApplyTo);

			// The round trip is what proves the reading: a wrong split would not put these bytes back.
			Assert.Equal(wire, MagicBuffCodec.Build(parsed));
		}

		// The ordinary form is unaffected - a caster still round-trips as itself.
		[Fact]
		public void ACasterStillRoundTrips()
		{
			var wire = Convert.FromHexString(CapturedApply);
			wire.WriteU32Be(18, 0xC350); // give it back a class...
			wire.WriteU32Be(22, 0x1101B4F5); // ...and a caster

			var parsed = MagicBuffCodec.Parse(wire);

			Assert.Equal(0x1101B4F5u, parsed.ApplyFrom);
			Assert.Equal(wire, MagicBuffCodec.Build(parsed));
		}

		// A zero class with a non-zero instance is neither a Character nor "no caster". It stays an error rather than
		// being quietly read as one - the point is to accept a state retail actually writes, not to stop checking.
		[Fact]
		public void AZeroClassWithAnInstance_IsStillRejected()
		{
			var wire = Convert.FromHexString(CapturedApply);
			wire.WriteU32Be(22, 0x0000BEEF);

			Assert.Throws<CodecParseException>(() => MagicBuffCodec.Parse(wire));
		}
	}
}
