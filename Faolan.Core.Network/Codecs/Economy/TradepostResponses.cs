using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.Economy
{
	// Server->client Tradepost responses. Endpoints/bodies validated byte-exact against the captures
	// (docs/subsystems/tradepost-protocol.md): the response Sender is the server's GameCharAgent endpoint, the Receiver is the
	// client's TradepostInterface (its instance is echoed from the inbound request, or the observed constant for a
	// server-initiated open).
	public static class TradepostResponses
	{
		// The client's TradepostInterface object instance - constant 0x08006F31 across both captures (different
		// chars/sessions), so it appears to be a deterministic client-side allocation. Used when the server opens
		// the tradepost without an inbound packet to echo (the auctioneer-interact / GM trigger). It is the same
		// TradepostInterface object id the mail endpoints carry (MailConstants.EndpointClass), so reuse that.
		public const uint DefaultInterfaceInstance = MailConstants.EndpointClass;

		private const float AccessRadius = 15.0f;

		// The captured DispatchSendMailResult message for a player SendMail (F500): a generic result remoteformat.
		private const string SendMailResultXml = "<remoteformat id=\"0\" category=\"0\" key=\"C4O?^XE&#39;^VC4O?^XE&#39;^V\" knubot=\"0\"  ></remoteformat>";

		// Server source endpoint for tradepost dispatches: GameCharAgent sub-id 0x59, constant across both
		// captures (same bytes as the new-character ready-for-play-screen sender).
		private const uint GameCharAgentInstance = 0x59;

		// TradepostItemSearch query: [u16 len][lang ascii][cat:u32][page:u32][cat2:u32][itemId:u32][filter+price...].
		// `cat` is NOT echoed back as the search-results `head`, as this used to say: that head is the protocol's
		// 0x3F1 list count, (rowCount+1) * 0x3F1 (TradepostListingCodec). The two just both read 0x7E2 in the
		// captures - a 1-row result is 2 * 0x3F1 = 0x7E2 exactly - which is what made it look echoed. What `cat`
		// means in the REQUEST is untested (every captured query carries the same value).
		public static SearchQuery ParseSearch(FaolanStream body)
		{
			if (body == null)
				return new SearchQuery("", ListFrame.ForCount(1), 0, 0);

			try
			{
				var lang = body.ReadString();
				var cat = body.ReadUInt32(); // category root; 0x7E2 in every captured query, meaning untested
				var page = body.ReadUInt32();
				body.ReadUInt32(); // cat2 (mirror of cat)
				var itemId = body.ReadUInt32();
				return new SearchQuery(lang, cat, page, itemId);
			}
			catch (Exception ex)
			{
				// A malformed query used to become an EMPTY search with a fabricated category, so the player silently
				// got wrong results and nothing anywhere recorded that we failed to read their request.
				throw new InvalidOperationException($"Auction-house search query is malformed ({body.Length} bytes): {ex.Message}", ex);
			}
		}

		// Push a mail (DispatchMailMessage 0x2002), built byte-exact from the Mail row by MailMessageCodec. The base
		// form (includeAttachments=false) is the mailbox-poll/preview; with attachments it carries the bought item.
		public static Task SendMailMessage(INetworkClient client, Mail mail, bool includeAttachments, uint interfaceInstance = DefaultInterfaceInstance)
		{
			return client.SendAsync(Build(TradepostInterfaceOpcodes.DispatchMailMessage, MailMessageCodec.Build(mail, includeAttachments), interfaceInstance));
		}

		// DispatchSendMailResult (0x2003) [u8 success][u8 error] - the result of a send/mark/delete op.
		public static Task SendMailResult(INetworkClient client, bool success, byte error = 0, uint interfaceInstance = DefaultInterfaceInstance)
		{
			return client.SendAsync(Build(TradepostInterfaceOpcodes.DispatchSendMailResult, [Convert.ToByte(success), error], interfaceInstance));
		}

		// DispatchSendMailResult for a player SendMail (0x2002 -> 0x2003): [u8 success][u16 len + remoteformat XML]
		// (byte-shape from the sell capture F500). success=0 lets the client show "couldn't send" (e.g. bad recipient).
		public static Task SendMailSendResult(INetworkClient client, bool success, uint interfaceInstance = DefaultInterfaceInstance)
		{
			var s = new FaolanStream();
			s.WriteByte(Convert.ToByte(success));
			s.WriteString(SendMailResultXml);
			return client.SendAsync(Build(TradepostInterfaceOpcodes.DispatchSendMailResult, s.ToArray(), interfaceInstance));
		}

		// Open the auction house at `position` (the auctioneer/player location) so the client's within-radius check
		// passes. The real trigger is the player interacting with an auctioneer NPC; for now a GM command supplies
		// the player's live position (the capture shows DispatchOpenTradepost precedes TradepostOpen).
		public static Task SendOpenTradepost(INetworkClient client, Vector3 position, uint interfaceInstance = DefaultInterfaceInstance)
		{
			return client.SendAsync(Build(TradepostInterfaceOpcodes.DispatchOpenTradepost, BuildAccessPointBody(position, AccessRadius), interfaceInstance));
		}

		// DispatchRetrieveMailResult (0x2004) - a single 0x01 byte (the "mailbox retrieved" ack in the capture).
		public static Task SendRetrieveMailResult(INetworkClient client, uint interfaceInstance = DefaultInterfaceInstance)
		{
			return client.SendAsync(Build(TradepostInterfaceOpcodes.DispatchRetrieveMailResult, [0x01], interfaceInstance));
		}

		// Reply to TradepostItemSearch with DB-backed listings for the searched item (rows built byte-exact by
		// TradepostListingCodec, cheapest first). Echoes the inbound TradepostInterface instance so the client
		// matches the reply to its window.
		//
		// head = (rowCount + 1) * 0x3F1 - the list-framing invariant the client uses to size the grid (#212).
		// Decoded from the buy-from-AH capture: a 78-row result carries head 0x1375F = 79*0x3F1, a 5-row result
		// 0x17A6 = 6*0x3F1, etc. The old code echoed the search CATEGORY here, which only equals (N+1)*0x3F1 for a
		// single row ((1+1)*0x3F1 == 2018 == cat 0x7E2 - the coincidence that hid the bug and passed the single-row
		// golden test), so EVERY multi-row result collapsed to one visible row. (The earlier "unique per-row handle"
		// theory was a red herring: retail's per-row ItemHandle is a SHARED per-item value and the seller Handle
		// repeats across rows, yet all rows render - the client keys rows by saleId, not handle. So the handles come
		// straight off the stored listing rows, which already carry the retail-shaped shared ItemHandle + seller Handle.)
		public static Task SendSearchResults(INetworkClient client, uint interfaceInstance, IReadOnlyCollection<TradepostListing> listings)
		{
			var head = ListFrame.ForCount(listings.Count); // the LIST COUNT, not a category echo - see TradepostListingCodec
			return client.SendAsync(Build(TradepostInterfaceOpcodes.DispatchTradepostSearchResults, TradepostListingCodec.Build(head, listings), interfaceInstance));
		}

		private static Rpc Build(TradepostInterfaceOpcodes method, byte[] body, uint interfaceInstance)
		{
			var sender = new ProtoEndpoint
			{
				Type = Endpoints.GameCharAgentType,
				Instance = GameCharAgentInstance
			};
			var receiver = new ProtoEndpoint
			{
				Type = Endpoints.TradepostInterfaceType,
				Instance = interfaceInstance
			};

			return new Rpc(sender, receiver, method, body);
		}

		private static byte[] BuildAccessPointBody(Vector3 position, float radius)
		{
			var s = new FaolanStream(new byte[16], true);
			s.WriteVector3(position);
			s.WriteFloat(radius);
			return s.ToArray();
		}

		public readonly record struct SearchQuery(string Lang, uint Category, uint Page, uint ItemId);
	}
}
