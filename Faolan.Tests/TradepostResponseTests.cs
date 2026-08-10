using System;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Protobuf;
using Faolan.GameServer.Economy;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// Locks the tradepost server->client replies to the retail wire format, driving the LIVE send path
	// (TradepostResponses.SendOpenTradepost / SendSearchResults) and asserting the framed packet the client
	// actually receives. Sender GameCharAgent{0xA9ECB95D,0x59}, receiver TradepostInterface{0xF2C2D5B5,
	// inst=0x08006F31}, opcode 0x2000. See docs/subsystems/tradepost-protocol.md + tools/capture/decode_tradepost.py.
	public class TradepostResponseTests
	{
		private const uint InterfaceInstance = 0x08006F31;

		// The captured DispatchOpenTradepost (auctionhouse pkt#935): the auctioneer access point = the Tortage
		// position + 15.0 radius. The live open builds the body from that position, so passing it reproduces the packet.
		[Fact]
		public async Task SendOpenTradepost_MatchesCapture()
		{
			const string expected = "0000002f0552eb23000000170a070d5db9eca91059120a0db5d5c2f210b1de81402000447d700c42e62e00441a8ddb41700000";
			var position = new Vector3(BitConverter.UInt32BitsToSingle(0x447d700cu), BitConverter.UInt32BitsToSingle(0x42e62e00u), BitConverter.UInt32BitsToSingle(0x441a8ddbu));

			var client = new RecordingNetworkClient(0);
			await TradepostResponses.SendOpenTradepost(client, position);

			Assert.Equal(expected, Convert.ToHexStringLower(Assert.Single(client.Sent)));
		}

		// The captured single-row DispatchTradepostSearchResults (NecroL80 auctionhouse pkt#1028: seller "Jocko",
		// item 0x755D, price 1,000,000). The live search builds the grid from DB listings via TradepostListingCodec,
		// so one Jocko listing reproduces the captured packet (head = (1+1)*0x3F1 = 0x7E2 + the row).
		[Fact]
		public async Task SendSearchResults_MatchesCapture()
		{
			const string expected = "000000d6e089b4f6000000170a070d5db9eca91059120a0db5d5c2f210b1de81402005000007e220d82b60902c95450c0457f3" +
			                        "00054a6f636b6f00000000000f4240000000010000755d000000000000000015da16700000008008c5aab28189ec8aec2012" +
			                        "05080010a1081204081a1001120708820210c0843d12090889021085aa8c" +
			                        "d1061a530a140de5971bd61556c078261d9280fde2255a5c7bf112140de5971bd61556c078261d9280fde2255a5c7bf1" +
			                        "1a140de5971bd61556c078261d9280fde2255a5c7bf125970a3e002d970a3e0038014500000000";

			var client = new RecordingNetworkClient(0);
			await TradepostResponses.SendSearchResults(client, InterfaceInstance, [JockoListing()]);

			Assert.Equal(expected, Convert.ToHexStringLower(Assert.Single(client.Sent)));
		}

		// The single captured auctionhouse row (pkt#1028) as a DB listing - shared with TradepostListingCodecTests.
		private static TradepostListing JockoListing()
		{
			return new TradepostListing
			{
				SaleId = 0x20d82b60902c9545,
				Handle = 0x0c0457f3,
				SellerName = "Jocko",
				Price = 1_000_000,
				Quantity = 1,
				ItemId = 0x755d,
				InstanceField = 0,
				ItemHandle = 0x15da1670,
				EntryFlags = 0x421,
				Timestamp = 0x6a231505,
				TemplateItem = new Item
				{
					Key = new ProtoDecryptionKey
					{
						Key1 = 0xd61b97e5,
						Key2 = 0x2678c056,
						Key3 = 0xe2fd8092,
						Key4 = 0xf17b5c5a
					},
					// #749: the wire's `Level` comes off the joined TEMPLATE now, not a per-listing column.
					Level = 1
				},
				Template = 0x3e0a97,
				ItemHash = 0
			};
		}
	}
}
