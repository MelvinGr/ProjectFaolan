using System;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.World;
using Xunit;

namespace Faolan.Tests
{
	// The ACG instance-entry exchange (#821) - the confirmation box retail puts up before moving the player into a
	// personal instance, and the client's answer.
	//
	// Oracle: xxx_mage-necromancer_login_play3, the Tortage harbour rowboat to White Sands Night (10803), which is the
	// ONLY capture of this flow in the whole corpus. The player narrated it as it happened - "about to enter white
	// sands night (via boat object) there is a confirmation to enter (saying bound to <charname>)" - and the arrival
	// carries an `InstanceOwnerAndResetTime` feedback token, so the box really is the instance bind prompt.
	//
	//   476.4 f4555 s DispatchACGTeleportChoice   (the box)
	//   524.5 f4732 c RequestACGTeleport          (yes - 48 s later)
	//   524.8       s IntendedTeleportIIR 10803
	//   525.0       s N3TeleportIIR -> spawn point "PlayerSpawnPoint"   (#814)
	//   525.4       s DispatchRedirectToServer gs013-nj4:7021
	public class AcgTeleportCodecTests
	{
		private const string CapturedChoice =
			"00002a330000c35001317cf8620000c7a600002a3300000000007c7568000000000000000000000000000000000000";

		private const string CapturedRequest = "620000c7a600002a3300000000007c75680000000000000000000000000000000000";

		[Fact]
		public void ParseChoice_ReadsTheDestinationAndRoundTrips()
		{
			var captured = Convert.FromHexString(CapturedChoice);

			var parsed = AcgTeleportChoiceCodec.Parse(captured);

			Assert.Equal(10803u, parsed.MapId); // White Sands Night
			Assert.Equal(0x01317CF8u, parsed.ClientInstance);
			Assert.Equal(Convert.ToHexStringLower(captured), Convert.ToHexStringLower(AcgTeleportChoiceCodec.Build(parsed)));
		}

		// ⚠️ The playfield CLASS word is what marks this destination as an ACG instance, and it is NOT the class an
		// ordinary cross-map announce carries (0xC79C). It is the only discriminator we have: `Maps.PlayfieldInstanced`
		// cannot serve, because it is 1 on 529 of the 530 maps we hold - ordinary doors point at "instanced" maps all
		// day, City of Tortage included.
		[Fact]
		public void TheChoicesProxy_CarriesTheAcgPlayfieldClass()
		{
			var parsed = AcgTeleportChoiceCodec.Parse(Convert.FromHexString(CapturedChoice));

			Assert.Equal(0x0000C7A6u, parsed.Proxy.ClassWord);
			Assert.NotEqual(PlayfieldProxy.PlayfieldClass, parsed.Proxy.ClassWord);
		}

		// The map is stated twice - leading word and proxy - so a record where they disagree is not this message.
		[Fact]
		public void ParseChoice_RejectsADisagreeingMap()
		{
			var corrupt = Convert.FromHexString(CapturedChoice.Replace("00002a330000c350", "00002a340000c350"));

			Assert.Throws<CodecParseException>(() => AcgTeleportChoiceCodec.Parse(corrupt));
		}

		[Fact]
		public void ParseRequest_IsTheChoicesProxyEchoedBack()
		{
			var captured = Convert.FromHexString(CapturedRequest);

			var parsed = AcgTeleportRequestCodec.Parse(captured);

			Assert.Equal(10803u, parsed.MapId);
			Assert.Equal(Convert.ToHexStringLower(captured), Convert.ToHexStringLower(AcgTeleportRequestCodec.Build(parsed)));
		}

		// The two halves really are the same proxy - which is what lets a handler answer the request without holding
		// per-player state about which box it put up.
		[Fact]
		public void TheRequestEchoesTheChoicesProxyExactly()
		{
			var choice = AcgTeleportChoiceCodec.Parse(Convert.FromHexString(CapturedChoice));
			var request = AcgTeleportRequestCodec.Parse(Convert.FromHexString(CapturedRequest));

			Assert.Equal(choice.Proxy, request.Proxy);
		}
	}
}
