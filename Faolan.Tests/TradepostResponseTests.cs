using Faolan.GameServer.Economy.Tradepost;
using Xunit;

namespace Faolan.Tests
{
	// Locks the DispatchOpenTradepost reply to the retail wire format. Expected = the full captured packet
	// (account-faolan-character-necrol80 ... auctionhouse, pkt#935): sender GameCharAgent{0xA9ECB95D,0x59},
	// receiver TradepostInterface{0xF2C2D5B5, inst=0x08006F31}, opcode 0x2000, body = the auctioneer access point
	// (Tortage pos + 15.0 radius). See docs/tradepost-protocol.md + Captures/scripts/decode_tradepost.py.
	public class TradepostResponseTests
	{
		[Fact]
		public void BuildOpenTradepost_MatchesCapture()
		{
			const string expected = "0000002f0552eb23000000170a070d5db9eca91059120a0db5d5c2f210b1de81402000447d700c42e62e00441a8ddb41700000";

			var bytes = TradepostResponses.BuildOpenTradepostBytes(0x08006F31);

			Assert.Equal(expected, ToHex(bytes));
		}

		// Expected = the full captured single-row DispatchTradepostSearchResults (necrol80 auctionhouse pkt#1028:
		// seller "Jocko", item 0x755D, price 1,000,000) — locks the PoC replay reply byte-for-byte.
		[Fact]
		public void BuildSearchResults_MatchesCapture()
		{
			const string expected = "000000d6e089b4f6000000170a070d5db9eca91059120a0db5d5c2f210b1de81402005000007e220d82b60902c95450c0457f3" +
			                        "00054a6f636b6f00000000000f4240000000010000755d000000000000000015da16700000008008c5aab28189ec8aec2012" +
			                        "05080010a1081204081a1001120708820210c0843d12090889021085aa8cd1061a530a140de5971bd61556c078261d9280fd" +
			                        "e2255a5c7bf112140de5971bd61556c078261d9280fde2255a5c7bf11a140de5971bd61556c078261d9280fde2255a5c7bf1" + "25970a3e002d970a3e0038014500000000";

			var bytes = TradepostResponses.BuildSearchResultsBytes(0x08006F31);

			Assert.Equal(expected, ToHex(bytes));
		}

		private static string ToHex(byte[] bytes)
		{
			var chars = new char[bytes.Length * 2];
			for (var i = 0; i < bytes.Length; i++)
			{
				const string hex = "0123456789abcdef";
				chars[i * 2] = hex[bytes[i] >> 4];
				chars[i * 2 + 1] = hex[bytes[i] & 0xF];
			}

			return new string(chars);
		}
	}
}
