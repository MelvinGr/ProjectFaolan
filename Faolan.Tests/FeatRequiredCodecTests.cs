using System;
using System.Text;
using Faolan.Core.Network.Codecs.Text;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// Byte-exact construct of retail's `FeatRequired` refusal (#779) - the on-screen line a player gets when they
	// swing at a resource node their gathering rank does not reach. Oracle:
	// `demol80-demonologist_login_gameplay_annotated_1`, frames 5644 and 5726, both naming feat template 894305
	// (`Stonecutting Adept`) after a swing at a Marble node, and both beside the player's own chat annotation
	// saying exactly that.
	public class FeatRequiredCodecTests
	{
		// frame 5644, the 322-byte FeatRequired packet addressed to instance 0x110099bd. Frame 5726 is byte-identical
		// apart from being 14 s later, which is itself the evidence the keys are constants and not per-send nonces.
		private const string StonecuttingAdeptRefusal =
			"0000013e8d5a8b6b0000c350110099bd01000000000000000000000000011f3c72656d6f7465666f726d617420746f6b656e3d22" +
			"466561745265717569726564222063617465676f72793d2231303022206b65793d2253622126616d703b55313f296172504d3142" +
			"26616d703b2a342c764722206b6e75626f743d22302220203e3c706172616d657465722076616c75653d22373b266c743b72656d" +
			"6f7465666f726d61742069643d2671756f743b3839343330352671756f743b2063617465676f72793d2671756f743b3530323230" +
			"2671756f743b206b65793d2671756f743b406e485d6329755b25314a716b2c5562544e48732671756f743b206b6e75626f743d26" +
			"71756f743b302671756f743b20202667743b266c743b2f72656d6f7465666f726d61742667743b22202f3e3c2f72656d6f746566" + "6f726d61743e00000001";

		[Fact]
		public void Build_MatchesTheCapturedStonecuttingRefusal()
		{
			var body = FormatFeedbackCodec.Build(0x110099bd, new FeatRequired(894305));

			Assert.Equal(CapturedRecord.Hex(StonecuttingAdeptRefusal), Convert.ToHexStringLower(body));
		}

		[Fact]
		public void Build_NamesAnyFeat_AndParsesBack()
		{
			// The generalisation the plaintext feat-name category buys (#779): only the id changes.
			var body = FormatFeedbackCodec.Build(0x12345678, new FeatRequired(893925));
			var parsed = FormatFeedbackCodec.Parse(body);

			Assert.Equal(0x12345678u, parsed.Instance);
			Assert.Equal(FormatFeedbackCodec.FifoBannerTypeFlags, parsed.TypeFlags);
			Assert.Equal(1u, parsed.Footer);

			var xml = Encoding.Latin1.GetString(parsed.Xml);
			Assert.Contains("token=\"FeatRequired\"", xml);
			Assert.Contains("id=&quot;893925&quot;", xml);
			Assert.Contains("category=&quot;50220&quot;", xml);
		}
	}
}
