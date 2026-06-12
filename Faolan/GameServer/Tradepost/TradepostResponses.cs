using System;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Data;
using Faolan.Core.Network;
using Faolan.Core.Network.Conan;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Protobuf;

namespace Faolan.GameServer.Tradepost
{
	// Server->client Tradepost responses. Endpoints/bodies validated byte-exact against the captures
	// (docs/tradepost-protocol.md): the response Sender is the server's GameCharAgent endpoint, the Receiver is the
	// client's TradepostInterface (its instance is echoed from the inbound request, or the observed constant for a
	// server-initiated open).
	public static class TradepostResponses
	{
		// Server source endpoint for tradepost dispatches: GameCharAgent (hash "GameCharAgent") sub-id 0x59,
		// constant across both captures (same bytes as NewCharacterReadyForPlayScreenSender).
		private const uint GameCharAgentType = ConanEndpoints.GameSpawnCallerType; // 0xA9ECB95D
		private const uint GameCharAgentInstance = 0x59;

		// The client's TradepostInterface object instance - constant 0x08006F31 across both captures (different
		// chars/sessions), so it appears to be a deterministic client-side allocation. Used when the server opens
		// the tradepost without an inbound packet to echo (the auctioneer-interact / GM trigger).
		public const uint DefaultInterfaceInstance = 0x08006F31;

		private const float AccessRadius = 15.0f;

		// DispatchOpenTradepost body = the auctioneer access point [posX][posY][posZ][maxRadius] (big-endian floats).
		// This captured value (Tortage auctioneer + 15.0) is kept only as the golden-test oracle; live opens build
		// the body from the player's position (BuildAccessPointBody) so the client's within-radius check passes.
		private static readonly byte[] CapturedAccessPointBody =
		{
			0x44, 0x7D, 0x70, 0x0C, 0x42, 0xE6, 0x2E, 0x00, 0x44, 0x1A, 0x8D, 0xDB, 0x41, 0x70, 0x00, 0x00
		};

		// DispatchTradepostSearchResults body - PoC REPLAY of a captured single-row listing (necrol80 auctionhouse
		// pkt#1028: seller "Jocko", item 0x755D, price 1,000,000, qty 1, + the item's ProtoAcgItem). [head:u32][row]*
		// per docs/tradepost-protocol.md. Step 3 replaces this with DB-built rows (TradepostListing + the existing
		// ProtoAcgItem builder); the embedded blob is the interim, like the beach seed (TODO #7 data-as-code).
		private static readonly byte[] SearchResultsReplayBody = Convert.FromHexString(
			"000007e220d82b60902c95450c0457f300054a6f636b6f00000000000f4240000000010000755d000000000000000015" +
			"da16700000008008c5aab28189ec8aec201205080010a1081204081a1001120708820210c0843d12090889021085aa8c" +
			"d1061a530a140de5971bd61556c078261d9280fde2255a5c7bf112140de5971bd61556c078261d9280fde2255a5c7bf1" +
			"1a140de5971bd61556c078261d9280fde2255a5c7bf125970a3e002d970a3e0038014500000000");

		// Open the auction house at `position` (the auctioneer/player location) so the client's within-radius check
		// passes. The real trigger is the player interacting with an auctioneer NPC; for now a GM command supplies
		// the player's live position (the capture shows DispatchOpenTradepost precedes TradepostOpen).
		public static Task SendOpenTradepost(INetworkClient client, Vector3 position, uint interfaceInstance = DefaultInterfaceInstance) =>
			Build(TradepostProtocol.InterfaceMethod.DispatchOpenTradepost, BuildAccessPointBody(position, AccessRadius), interfaceInstance)
				.SendAsync(client);

		// Reply to TradepostItemSearch so the auction-house listing grid renders. PoC: replays a captured listing
		// regardless of the query (the client renders whatever rows we send). Step 3 builds rows from DB listings
		// matching the searched itemId. Echoes the inbound TradepostInterface instance.
		public static Task SendSearchResults(INetworkClient client, ConanPacket inbound) =>
			Build(TradepostProtocol.InterfaceMethod.DispatchSearchResults, SearchResultsReplayBody, inbound.Sender.Instance)
				.SendAsync(client);

		private static byte[] BuildAccessPointBody(Vector3 position, float radius)
		{
			var s = new FaolanStream(new byte[16], writable: true);
			s.WriteFloatBigEndian(position.X);
			s.WriteFloatBigEndian(position.Y);
			s.WriteFloatBigEndian(position.Z);
			s.WriteFloatBigEndian(radius);
			return s.ToArray();
		}

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
		// Open uses the captured access-point body (the oracle); search uses the replay body.
		public static byte[] BuildOpenTradepostBytes(uint interfaceInstance) =>
			Finalize(Build(TradepostProtocol.InterfaceMethod.DispatchOpenTradepost, CapturedAccessPointBody, interfaceInstance));

		public static byte[] BuildSearchResultsBytes(uint interfaceInstance) =>
			Finalize(Build(TradepostProtocol.InterfaceMethod.DispatchSearchResults, SearchResultsReplayBody, interfaceInstance));

		private static byte[] Finalize(PacketStream stream)
		{
			stream.WriteLengthHash();
			return stream.ToArray();
		}
	}
}
