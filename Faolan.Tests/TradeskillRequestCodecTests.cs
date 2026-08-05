using System;
using Faolan.Core.Network.Codecs.Economy;
using Xunit;

namespace Faolan.Tests
{
	// 0x06B9CD94 TradeskillRequestIIR - the recipe-book request (#148). The body below is retail's, verbatim from
	// NecroL80_mage-necromancer_shops_open-crrafting-receipies, which is the ONLY tradeskill traffic in all 120 dumps.
	public class TradeskillRequestCodecTests
	{
		// The full 0x2000 sub-payload: [u32 iirLength = 0x11][u32 subop][u32 0xC350][u32 handle][u8 env][u32 version].
		private const string CapturedRequest = "0000001106b9cd940000c3500802e5d40000000001";

		[Fact]
		public void CapturedRequest_RoundTripsByteExact()
		{
			var payload = Convert.FromHexString(CapturedRequest);
			var body = payload.AsSpan(sizeof(uint)); // drop the length prefix - Build never writes it

			var parsed = TradeskillRequestCodec.Parse(body);

			Assert.Equal(0x0802E5D4u, parsed.Handle);
			Assert.Equal(0, parsed.Env);
			Assert.Equal(TradeskillRequestCodec.Build(parsed), body.ToArray());
		}

		// The point of parsing rather than discarding: the request is FULLY accounted for at 17 bytes, so it cannot
		// be carrying a recipe id. The client's own reader takes one u32 after the env byte and nothing else.
		[Fact]
		public void CapturedRequest_ConsumesEveryByte()
		{
			var body = Convert.FromHexString(CapturedRequest).AsSpan(sizeof(uint));

			Assert.Equal(17, body.Length);
			Assert.NotNull(TradeskillRequestCodec.Parse(body));
		}

		[Fact]
		public void Parse_RejectsAVersionTheClientWouldReject()
		{
			var payload = Convert.FromHexString(CapturedRequest);
			payload[^1] = 2; // the client logs "TradeskillRequestIIR_c::ReadSubClass version mismatch" and stops

			Assert.ThrowsAny<Exception>(() => TradeskillRequestCodec.Parse(payload.AsSpan(sizeof(uint))));
		}
	}
}
