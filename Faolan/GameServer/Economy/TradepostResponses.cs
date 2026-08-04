using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Protobuf;

namespace Faolan.GameServer.Economy
{
	// Server->client Tradepost SENDS. Endpoints validated byte-exact against the captures
	// (docs/subsystems/tradepost-protocol.md): the response Sender is the server's GameCharAgent endpoint, the Receiver
	// is the client's TradepostInterface (its instance echoed from the inbound request, or the observed constant for a
	// server-initiated open).
	//
	// This USED to live in `Codecs/Economy/` - six `INetworkClient -> Task` methods in the codec folder, the mirror
	// image of #530 §9 B. The bodies are codecs now (`MailMessageCodec`, `TradepostListingCodec`, `OpenTradepostCodec`,
	// `SendMailResultCodec`) and only the addressing + the send are left here, which is listener work.
	public static class TradepostResponses
	{
		// The client's TradepostInterface object instance - constant 0x08006F31 across both captures (different
		// chars/sessions), so it appears to be a deterministic client-side allocation. Used when the server opens the
		// tradepost without an inbound packet to echo (the auctioneer-interact / GM trigger). It is the same
		// TradepostInterface object id the mail endpoints carry (MailConstants.EndpointClass), so reuse that.
		public const uint DefaultInterfaceInstance = MailConstants.EndpointClass;

		// Server source endpoint for tradepost dispatches: GameCharAgent sub-id 0x59, constant across both captures
		// (the same bytes as the new-character ready-for-play-screen sender).
		private const uint GameCharAgentInstance = 0x59;

		// The captured DispatchRetrieveMailResult body: a single 0x01 (the "mailbox retrieved" ack). A fieldless
		// captured literal, so it stays a constant rather than becoming a codec with a parameterless record - the
		// treatment #530 wave 2 gave LostGraceEffect.
		private static readonly byte[] RetrieveMailAck = [0x01];

		// Push a mail (DispatchMailMessage 0x2002). The base form (includeAttachments=false) is the mailbox-poll /
		// preview; with attachments it carries the bought item.
		public static Task SendMailMessage(INetworkClient client, Mail mail, bool includeAttachments, uint interfaceInstance = DefaultInterfaceInstance)
		{
			return client.SendAsync(Build(TradepostInterfaceMethods.DispatchMailMessage, MailMessageCodec.Build(mail, includeAttachments), interfaceInstance));
		}

		// DispatchSendMailResult (0x2003) - the result of a mark/delete/cancel op.
		public static Task SendMailResult(INetworkClient client, bool success, byte error = 0, uint interfaceInstance = DefaultInterfaceInstance)
		{
			return client.SendAsync(Build(TradepostInterfaceMethods.DispatchSendMailResult, SendMailResultCodec.Build(new MailOpResult(success, error)), interfaceInstance));
		}

		// DispatchSendMailResult for a player SendMail (0x2002 -> 0x2003). success=0 lets the client show
		// "couldn't send" (e.g. a bad recipient).
		public static Task SendMailSendResult(INetworkClient client, bool success, uint interfaceInstance = DefaultInterfaceInstance)
		{
			return client.SendAsync(Build(TradepostInterfaceMethods.DispatchSendMailResult, SendMailResultCodec.Build(SendMailResults.Send(success)), interfaceInstance));
		}

		// Open the auction house at `position` (the auctioneer/player location) so the client's within-radius check
		// passes. The real trigger is the player interacting with an auctioneer NPC; for now a GM command supplies the
		// player's live position (the capture shows DispatchOpenTradepost precedes TradepostOpen).
		public static Task SendOpenTradepost(INetworkClient client, Vector3 position, uint interfaceInstance = DefaultInterfaceInstance)
		{
			return client.SendAsync(Build(TradepostInterfaceMethods.DispatchOpenTradepost, OpenTradepostCodec.Build(position), interfaceInstance));
		}

		// DispatchRetrieveMailResult (0x2004).
		public static Task SendRetrieveMailResult(INetworkClient client, uint interfaceInstance = DefaultInterfaceInstance)
		{
			return client.SendAsync(Build(TradepostInterfaceMethods.DispatchRetrieveMailResult, RetrieveMailAck, interfaceInstance));
		}

		// Reply to TradepostItemSearch with DB-backed listings for the searched item (rows built byte-exact by
		// TradepostListingCodec, cheapest first). Echoes the inbound TradepostInterface instance so the client matches
		// the reply to its window.
		//
		// head = (rowCount + 1) * 0x3F1 - the list-framing invariant the client uses to size the grid (#212). Decoded
		// from the buy-from-AH capture: a 78-row result carries head 0x1375F = 79*0x3F1, a 5-row result 0x17A6 =
		// 6*0x3F1, etc. The old code echoed the search CATEGORY here, which only equals (N+1)*0x3F1 for a single row
		// ((1+1)*0x3F1 == 2018 == cat 0x7E2 - the coincidence that hid the bug and passed the single-row golden test),
		// so EVERY multi-row result collapsed to one visible row. (The earlier "unique per-row handle" theory was a red
		// herring: retail's per-row ItemHandle is a SHARED per-item value and the seller Handle repeats across rows, yet
		// all rows render - the client keys rows by saleId, not handle. So the handles come straight off the stored
		// listing rows, which already carry the retail-shaped shared ItemHandle + seller Handle.)
		public static Task SendSearchResults(INetworkClient client, uint interfaceInstance, IReadOnlyCollection<TradepostListing> listings)
		{
			var head = ListFrame.ForCount(listings.Count); // the LIST COUNT, not a category echo - see TradepostListingCodec

			return client.SendAsync(Build(TradepostInterfaceMethods.DispatchTradepostSearchResults, TradepostListingCodec.Build(head, listings), interfaceInstance));
		}

		// Pushes one bank-slot stat update (0x201c on container 105): [kind=105][charInstance][slot][statId][value],
		// byte-exact to the capture (F376/F377/F364/F398). Rides the same GameCharAgent conversation as the bank
		// slot registrations (BankHandler), so the client applies it to the live bank cell.
		//
		// The AH/mail overlays are its only callers, so it lives here rather than on the listener's Outbound builders.
		public static Task SendBankSlotStat(INetworkClient client, uint slot, uint statId, uint value)
		{
			return client.SendAsync(new Rpc(Endpoints.Endpoint(Endpoints.GameCharAgentType, 0x58), client.Endpoint(Endpoints.GameCharInterfaceType), GameCharInterfaceMethods.DispatchSetItemStat,
				SetItemStatCodec.Build(SetItemStats.BankSlot(client.Session.Character.Id, slot, statId, value))));
		}

		private static Rpc Build(TradepostInterfaceMethods method, byte[] body, uint interfaceInstance)
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

			return new Rpc(sender, receiver, (ushort)method, body);
		}
	}
}
