using System;
using System.Threading.Tasks;
using Faolan.Core.Network;
using Faolan.Core.Network.Conan;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Protobuf;

namespace Faolan.GameServer.Tradepost
{
	// Server->client Tradepost responses. Endpoints/bodies validated byte-exact against the captures
	// (docs/tradepost-protocol.md): the response Sender is the server's GameCharAgent endpoint, the Receiver is the
	// client's TradepostInterface (its instance is echoed from the inbound request's Sender).
	public static class TradepostResponses
	{
		// Server source endpoint for tradepost dispatches: GameCharAgent (hash "GameCharAgent") sub-id 0x59,
		// constant across both captures (same bytes as NewCharacterReadyForPlayScreenSender).
		private const uint GameCharAgentType = ConanEndpoints.GameSpawnCallerType; // 0xA9ECB95D
		private const uint GameCharAgentInstance = 0x59;

		// DispatchOpenTradepost body = the auctioneer access point: [posX][posY][posZ][maxRadius] as big-endian
		// floats. PoC value = the captured Tortage tradepost location + 15.0 radius (DB-driven from the auctioneer
		// NPC in Step 3). The client only opens the window when the player is within maxRadius of this point.
		private static readonly byte[] AccessPointBody =
		{
			0x44, 0x7D, 0x70, 0x0C, // X = 1013.75
			0x42, 0xE6, 0x2E, 0x00, // Y = 115.09
			0x44, 0x1A, 0x8D, 0xDB, // Z = 618.22
			0x41, 0x70, 0x00, 0x00  // maxRadius = 15.0
		};

		// DispatchTradepostSearchResults body — PoC REPLAY of a captured single-row listing (necrol80 auctionhouse
		// pkt#1028: seller "Jocko", item 0x755D, price 1,000,000, qty 1, + the item's ProtoAcgItem). [head:u32][row]*
		// per docs/tradepost-protocol.md. Step 3 replaces this with DB-built rows (TradepostListing + the existing
		// ProtoAcgItem builder); the embedded blob is the interim, like the beach seed (TODO #7 data-as-code).
		private static readonly byte[] SearchResultsReplayBody = Convert.FromHexString(
			"000007e220d82b60902c95450c0457f300054a6f636b6f00000000000f4240000000010000755d000000000000000015" +
			"da16700000008008c5aab28189ec8aec201205080010a1081204081a1001120708820210c0843d12090889021085aa8c" +
			"d1061a530a140de5971bd61556c078261d9280fde2255a5c7bf112140de5971bd61556c078261d9280fde2255a5c7bf1" +
			"1a140de5971bd61556c078261d9280fde2255a5c7bf125970a3e002d970a3e0038014500000000");

		// Reply to TradepostOpen so the client opens the auction-house window. `inbound` is the client's
		// TradepostOpen packet (its Sender = the client's TradepostInterface, whose instance we echo back).
		public static Task SendOpenTradepost(INetworkClient client, ConanPacket inbound) =>
			Build(TradepostProtocol.InterfaceMethod.DispatchOpenTradepost, AccessPointBody, inbound.Sender.Instance)
				.SendAsync(client);

		// Reply to TradepostItemSearch so the auction-house listing grid renders. PoC: replays a captured listing
		// regardless of the query (the client renders whatever rows we send). Step 3 builds rows from DB listings
		// matching the searched itemId.
		public static Task SendSearchResults(INetworkClient client, ConanPacket inbound) =>
			Build(TradepostProtocol.InterfaceMethod.DispatchSearchResults, SearchResultsReplayBody, inbound.Sender.Instance)
				.SendAsync(client);

		private static PacketStream Build(TradepostProtocol.InterfaceMethod method, byte[] body, uint interfaceInstance)
		{
			var sender = new ProtoEndpoint { Type = GameCharAgentType, Instance = GameCharAgentInstance };
			var receiver = new ProtoEndpoint { Type = TradepostProtocol.InterfaceType, Instance = interfaceInstance };

			var stream = new PacketStream();
			stream.WriteHeader(sender, receiver, method);
			stream.WriteArray(body);
			return stream;
		}

		// Finalized on-the-wire bytes (length + crc filled) — for byte-exact golden tests vs the captured packets.
		public static byte[] BuildOpenTradepostBytes(uint interfaceInstance) =>
			Finalize(Build(TradepostProtocol.InterfaceMethod.DispatchOpenTradepost, AccessPointBody, interfaceInstance));

		public static byte[] BuildSearchResultsBytes(uint interfaceInstance) =>
			Finalize(Build(TradepostProtocol.InterfaceMethod.DispatchSearchResults, SearchResultsReplayBody, interfaceInstance));

		private static byte[] Finalize(PacketStream stream)
		{
			stream.WriteLengthHash();
			return stream.ToArray();
		}
	}
}
