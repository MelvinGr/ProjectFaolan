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

		// Reply to TradepostOpen so the client opens the auction-house window. `inbound` is the client's
		// TradepostOpen packet (its Sender = the client's TradepostInterface, whose instance we echo back).
		public static Task SendOpenTradepost(INetworkClient client, ConanPacket inbound) =>
			BuildOpenTradepost(inbound.Sender.Instance).SendAsync(client);

		private static PacketStream BuildOpenTradepost(uint interfaceInstance)
		{
			var sender = new ProtoEndpoint { Type = GameCharAgentType, Instance = GameCharAgentInstance };
			var receiver = new ProtoEndpoint { Type = TradepostProtocol.InterfaceType, Instance = interfaceInstance };

			var stream = new PacketStream();
			stream.WriteHeader(sender, receiver, TradepostProtocol.InterfaceMethod.DispatchOpenTradepost);
			stream.WriteArray(AccessPointBody);
			return stream;
		}

		// Finalized on-the-wire bytes (length + crc filled) — for byte-exact golden tests vs the captured packet.
		public static byte[] BuildOpenTradepostBytes(uint interfaceInstance)
		{
			var stream = BuildOpenTradepost(interfaceInstance);
			stream.WriteLengthHash();
			return stream.ToArray();
		}
	}
}
