using Faolan.Core.Network.Conan;

namespace Faolan.GameServer.Economy.Tradepost
{
	// Auction house ("Tradepost") + mail wire protocol. Full grammar in docs/tradepost-protocol.md, decoded from
	// the necrol80/assassinl23 captures via Captures/scripts/decode_tradepost.py. Carried on the GameServer RPC layer (NOT a
	// separate connection): the method is the 0x20xx in ConanPacket.Opcode AFTER the sender/receiver endpoints.
	//
	// The method ids COLLIDE with ordinary GameServer opcodes (0x2000, 0x2005=Ping, ...), so tradepost packets must
	// be routed by their RPC class id, never by opcode alone. TradepostAgent is the SERVER object the client calls
	// (client->server); TradepostInterface is the CLIENT object the server dispatches to (server->client).
	public static class TradepostProtocol
	{
		public const uint AgentType = 0x3D744981; // TradepostAgent      (client -> server endpoint)
		public const uint InterfaceType = 0xF2C2D5B5; // TradepostInterface  (server -> client endpoint)

		// Client -> server methods (selector after the endpoints) - the complete recovered
		// TradepostAgent.client_to_server table (opcode = 0x2000 + position; alignment verified on
		// ItemSearch/BuyItem/Close). The behavior decoded in docs/tradepost-protocol.md is noted per line where
		// it differs from what the name suggests.
		public enum AgentMethod : ushort
		{
			GetAllMails = 0x2000, // the mailbox poll
			GetMailByID = 0x2001, // body [mailId:u64]
			SendMail = 0x2002,
			MarkMailRead = 0x2003, // also observed for the delete-side flow
			CheckNewMail = 0x2004,
			GetMailItems = 0x2005,
			GetMailItem = 0x2006, // body [mailId][itemId]
			GetItemData = 0x2007, // observed with a [mailId]-style body (was decoded as "GetMailItems")
			DeleteMail = 0x2008,
			CancelMail = 0x2009,
			TradepostSellItem = 0x200A,
			TradepostChangeItemPrice = 0x200B,
			TradepostCancelSellItem = 0x200C,
			ItemSearch = 0x200D, // TradepostItemSearch (query body)
			BuyItem = 0x200E, // TradepostBuyItem [saleId:u64][searchResultId:u64]
			TradepostOpen = 0x200F, // not observed in captures - the client opens with UpdateItemTimeouts instead
			Close = 0x2010, // TradepostClose (empty body)
			UpdateItemTimeouts = 0x2011, // TradepostUpdateItemTimeouts - sent (empty body) when the AH window opens
			SetAsyncEnabled = 0x2012,
			SetNumWorkerThreads = 0x2013
		}

		// Server -> client methods. The full TradepostInterface s2c table is now recovered from AgeOfConan.exe
		// (Ghidra dispatcher switch, docs/aoc-rpc-method-opcodes.md): 8 methods, base 0x2000. The string-extracted
		// catalog missed DispatchClose (0x2001, no-arg -> no "stream bad" string), which is why SearchResults was
		// off by one (recovered-as-0x2004, true 0x2005). Values below are the TRUE registration order.
		public enum InterfaceMethod : ushort
		{
			DispatchOpenTradepost = 0x2000, // [posX][posY][posZ][maxRadius] floats (big-endian)
			DispatchClose = 0x2001, // no-arg (the catalog-missed insertion)
			DispatchMailMessage = 0x2002, // [mailId][recipient ep][sender ep][remoteformat body](+attachment)
			DispatchSendMailResult = 0x2003, // [bSuccess][cError]
			DispatchRetrieveMailResult = 0x2004, // (was nicknamed "Ack" - body 0x01)
			DispatchSearchResults = 0x2005, // DispatchTradepostSearchResults; [head:u32][row]* (see TradepostSearchResults)
			DispatchDeleteCanceledMail = 0x2006,
			DispatchDeleteCanceledMailItem = 0x2007
		}

		// Inbound (client->server) tradepost packets address the TradepostAgent endpoint. We accept either field
		// matching, to be robust to caller/endpoint ordering in the header.
		public static bool IsInbound(ConanPacket packet)
		{
			return packet.Receiver.Type == AgentType || packet.Sender.Type == InterfaceType;
		}
	}
}
