using System;
using Faolan.GameServer.Combat;
using Faolan.GameServer.Economy.Shop;
using Xunit;
using Faolan.Core.Network.Codecs.GameServer;

namespace Faolan.Tests
{
	// Byte-exact gates for the vendor trade-OPEN reply pair (#134), against the NecroL80
	// sell_items_to_trader-intract-with-auctionhouse capture: the client's TalkToNpc Interact at the vendor
	// (frame 1188, player 0x08006F31 -> vendor 0x000439AD) is answered by the 0x0D155695 trade-select echo
	// (frame 1189) and the BARE BCC echo-ack (frame 1193 first record - the standard behavior echo without
	// the combat ack's 12-zero pad), then DispatchShopData (already covered by ShopTests).
	public class ShopOpenBuilderTests
	{
		[Fact]
		public void BareBehaviorEchoAck_IsByteIdenticalToFrame1193()
		{
			// The inbound Interact exactly as received (frame 1188, incl. its 4-byte length prefix).
			var inbound = Convert.FromHexString("0000002e642cd3d60000c35008006f31000300000016000000140898c8021002280a3000520808d0860310adf3100000000a");
			// Retail's ack (frame 1193, first record): seq+1, args re-length'd with field3 = the request's own
			// Mode (2) spliced in, the 4-byte tail echoed bare - no zero pad.
			var captured = Convert.FromHexString("642cd3d60000c35008006f31000400000016000000160898c80210021802280a3000520808d0860310adf3100000000a");

			var built = BehaviorAckBody.BuildBehaviorEchoAckBody(inbound, 0x08006F31, 2, padTail: false);

			Assert.Equal(captured, built);
		}

		[Fact]
		public void BuildTradeSelectEcho_IsByteIdenticalToFrame1189()
		{
			var captured = Convert.FromHexString("0d1556950000c35008006f31000000c350000439ad0000000000000002");

			var built = ShopOpenBuilder.BuildTradeSelectEcho(0x08006F31, 0x000439AD);

			Assert.Equal(captured, built);
		}
	}
}
