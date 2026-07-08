using System;
using Xunit;
using StockConversationService = Faolan.GameServer.Dialogue.StockConversationService;

namespace Faolan.Tests
{
	// #234 create-cutscene speech: the s2c ReceivedStockPhraseText (0x205D) reply to the client's
	// PlayStockphraseAsSubtitle request. Golden-gated against the retail create capture
	// (Blablabla_soldier-guardian_create_*): the built body must be byte-identical to the captured
	// 0x205D bodies once the live threadCtx is echoed. Proves both the request parse and the response
	// build reproduce retail exactly (only the per-session threadCtx differs).
	public class StockPhraseResponderTests
	{
		// (stockKey, capturedThreadCtx, capturedFullResponseBody) triples from the guardian create capture.
		[Theory]
		[InlineData("{stock_48}", 0x000077aau, "000077aa00623c72656d6f7465666f726d61742069643d2236313935222063617465676f72793d22353030303022206b65793d22676456765a2b5844616979714f415f4f512b5f2e22206b6e75626f743d223338382220203e3c2f72656d6f7465666f726d61743e")]
		[InlineData("{stock_49}", 0x000086a7u, "000086a700663c72656d6f7465666f726d61742069643d2236313936222063617465676f72793d22353030303022206b65793d22556a317626616d703b6677456f714e5f6c4138626e574f7022206b6e75626f743d223338382220203e3c2f72656d6f7465666f726d61743e")]
		[InlineData("{stock_50}", 0x0000ab40u, "0000ab4000623c72656d6f7465666f726d61742069643d2236313937222063617465676f72793d22353030303022206b65793d224f2c41794f542949376b542b4543724b28562e3f22206b6e75626f743d223338382220203e3c2f72656d6f7465666f726d61743e")]
		[InlineData("{stock_51}", 0x0000ab40u, "0000ab40006a3c72656d6f7465666f726d61742069643d2236313938222063617465676f72793d22353030303022206b65793d224b262333393b422a4c4b535a2a2925262339323b6d725e5e7766305e22206b6e75626f743d223338382220203e3c2f72656d6f7465666f726d61743e")]
		[InlineData("{stock_52}", 0x0000cfbbu, "0000cfbb00623c72656d6f7465666f726d61742069643d2236313939222063617465676f72793d22353030303022206b65793d22212d7536322e2b4e45602860656e6671612d4d4822206b6e75626f743d223338382220203e3c2f72656d6f7465666f726d61743e")]
		[InlineData("{stock_56}", 0x0000e243u, "0000e24300623c72656d6f7465666f726d61742069643d2236323030222063617465676f72793d22353030303022206b65793d226c3059454128335845626c6f7934486b7548464322206b6e75626f743d223338382220203e3c2f72656d6f7465666f726d61743e")]
		public void ReceivedStockPhraseBody_MatchesCapture(string stockKey, uint threadCtx, string expectedHex)
		{
			Assert.True(StockConversationService.TryGetStockPhraseText(stockKey, out var text));
			var body = StockConversationService.BuildReceivedStockPhraseBody(threadCtx, text);
			Assert.Equal(expectedHex, Convert.ToHexStringLower(body));
		}

		// The request parse recovers the {stock_N} key + the per-session threadCtx from a captured 0x205E body.
		[Fact]
		public void ParseRequest_RecoversStockKeyAndThreadContext()
		{
			// captured c2s 0x205E: npcId {0xC350,0x2C1C} + "{stock_48}" + threadCtx 0x000077AA
			var req = Convert.FromHexString("0000c35000002c1c000a7b73746f636b5f34387d000077aa");
			Assert.True(StockConversationService.TryParsePlayStockphraseRequest(req, out var key, out var tc));
			Assert.Equal("{stock_48}", key);
			Assert.Equal(0x000077aau, tc);
		}
	}
}
