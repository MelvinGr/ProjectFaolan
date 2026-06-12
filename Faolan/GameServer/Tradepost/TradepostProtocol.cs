using Faolan.Core.Network.Conan;

namespace Faolan.GameServer.Tradepost
{
	// Auction house ("Tradepost") + mail wire protocol. Full grammar in docs/tradepost-protocol.md, decoded from
	// the necrol80/assassinl23 captures via Captures/decode_tradepost.py. Carried on the GameServer RPC layer (NOT a
	// separate connection): the method is the 0x20xx in ConanPacket.Opcode AFTER the sender/receiver endpoints.
	//
	// The method ids COLLIDE with ordinary GameServer opcodes (0x2000, 0x2005=Ping, ...), so tradepost packets must
	// be routed by their RPC class id, never by opcode alone. TradepostAgent is the SERVER object the client calls
	// (client->server); TradepostInterface is the CLIENT object the server dispatches to (server->client).
	public static class TradepostProtocol
	{
		public const uint AgentType = 0x3D744981;     // TradepostAgent      (client -> server endpoint)
		public const uint InterfaceType = 0xF2C2D5B5; // TradepostInterface  (server -> client endpoint)

		// Client -> server methods (selector after the endpoints). See docs/tradepost-protocol.md method map.
		public enum AgentMethod : ushort
		{
			Open = 0x2011,        // TradepostOpen (empty body)
			ItemSearch = 0x200D,  // TradepostItemSearch (query body)
			BuyItem = 0x200E,     // TradepostBuyItem [saleId:u64][searchResultId:u64]
			Close = 0x2010,       // TradepostClose (empty body)
			// Mail ops (body = [mailId:u64], or [mailId][itemId] for GetMailItem):
			GetMail = 0x2001,
			GetMailItems = 0x2007,
			GetMailItem = 0x2006,
			DeleteOrMarkMail = 0x2003,
			MailPoll = 0x2000
		}

		// Server -> client methods.
		public enum InterfaceMethod : ushort
		{
			DispatchOpenTradepost = 0x2000,          // [posX][posY][posZ][maxRadius] floats (big-endian)
			DispatchSearchResults = 0x2005,          // [head:u32][row]* (see TradepostSearchResults)
			DispatchMailMessage = 0x2002,            // [mailId][recipient ep][sender ep][remoteformat body](+attachment)
			Ack = 0x2004,                            // 0x01
			DispatchClose = 0x2001
		}

		// Inbound (client->server) tradepost packets address the TradepostAgent endpoint. We accept either field
		// matching, to be robust to caller/endpoint ordering in the header.
		public static bool IsInbound(ConanPacket packet) =>
			packet.Receiver.Type == AgentType || packet.Sender.Type == InterfaceType;
	}
}
