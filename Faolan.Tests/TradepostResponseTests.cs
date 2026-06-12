using Faolan.GameServer.Tradepost;
using Xunit;

namespace Faolan.Tests
{
	// Locks the DispatchOpenTradepost reply to the retail wire format. Expected = the full captured packet
	// (account-faolan-character-necrol80 ... auctionhouse, pkt#935): sender GameCharAgent{0xA9ECB95D,0x59},
	// receiver TradepostInterface{0xF2C2D5B5, inst=0x08006F31}, opcode 0x2000, body = the auctioneer access point
	// (Tortage pos + 15.0 radius). See docs/tradepost-protocol.md + Captures/decode_tradepost.py.
	public class TradepostResponseTests
	{
		[Fact]
		public void BuildOpenTradepost_MatchesCapture()
		{
			const string expected =
				"0000002f0552eb23000000170a070d5db9eca91059120a0db5d5c2f210b1de81402000447d700c42e62e00441a8ddb41700000";

			var bytes = TradepostResponses.BuildOpenTradepostBytes(0x08006F31);

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
