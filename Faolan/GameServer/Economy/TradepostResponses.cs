using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Constants;
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
	/// <summary>
	///     The server-to-client tradepost sends: the addressing and the send, with the bodies built by their own
	///     codecs.
	/// </summary>
	/// <remarks>
	///     Endpoints are validated byte-exact against the captures (docs/subsystems/tradepost-protocol.md): the
	///     response sender is the server's GameCharAgent endpoint, and the receiver is the client's TradepostInterface,
	///     with its instance echoed from the inbound request or the observed constant for a server-initiated open.
	/// </remarks>
	public static class TradepostResponses
	{
		// The client's TradepostInterface object instance - constant 0x08006F31 across both captures, on different
		// characters and sessions, so it appears to be a deterministic client-side allocation. Used when the server
		// opens the tradepost with no inbound packet to echo (the auctioneer interact, or the GM trigger). It is the
		// same TradepostInterface object id the mail endpoints carry.
		public const uint DefaultInterfaceInstance = MailConstants.EndpointClass;

		// The server source endpoint for tradepost dispatches: GameCharAgent sub-id 0x59, constant across both
		// captures, and the same bytes as the new-character ready-for-play-screen sender.
		private const uint GameCharAgentInstance = 0x59;

		// The captured DispatchRetrieveMailResult body: a single 0x01, the "mailbox retrieved" ack. A fieldless
		// captured literal, so it stays a constant rather than becoming a codec over a parameterless record.
		private static readonly byte[] RetrieveMailAck = [0x01];

		/// <summary>
		///     Push a mail (DispatchMailMessage 0x2002). Without attachments this is the mailbox poll or preview; with
		///     them it carries the bought item.
		/// </summary>
		public static Task SendMailMessage(INetworkClient client, Mail mail, bool includeAttachments, uint interfaceInstance = DefaultInterfaceInstance)
		{
			return client.SendAsync(Build(TradepostInterfaceMethods.DispatchMailMessage, MailMessageCodec.Build(mail, includeAttachments), interfaceInstance));
		}

		/// <summary>DispatchSendMailResult (0x2003) - the result of a mark, delete or cancel op.</summary>
		public static Task SendMailResult(INetworkClient client, bool success, byte error = 0, uint interfaceInstance = DefaultInterfaceInstance)
		{
			return client.SendAsync(Build(TradepostInterfaceMethods.DispatchSendMailResult, SendMailResultCodec.Build(new MailOpResult(success, error)), interfaceInstance));
		}

		/// <summary>
		///     DispatchSendMailResult for a player SendMail (0x2002 to 0x2003). A false
		///     <paramref name="success" /> lets the client show "couldn't send", for a bad recipient say.
		/// </summary>
		public static Task SendMailSendResult(INetworkClient client, bool success, uint interfaceInstance = DefaultInterfaceInstance)
		{
			return client.SendAsync(Build(TradepostInterfaceMethods.DispatchSendMailResult, SendMailResultCodec.Build(SendMailResults.Send(success)), interfaceInstance));
		}

		/// <summary>
		///     Open the auction house at <paramref name="position" /> - the auctioneer or player location - so the
		///     client's within-radius check passes.
		/// </summary>
		/// <remarks>
		///     The retail trigger is the player interacting with an auctioneer NPC; a GM command can supply the
		///     player's live position instead. The capture shows DispatchOpenTradepost preceding TradepostOpen.
		/// </remarks>
		public static Task SendOpenTradepost(INetworkClient client, Vector3 position, uint interfaceInstance = DefaultInterfaceInstance)
		{
			return client.SendAsync(Build(TradepostInterfaceMethods.DispatchOpenTradepost, OpenTradepostCodec.Build(position), interfaceInstance));
		}

		/// <summary>
		///     DispatchClose (0x2001) - the server's own close, with an empty body.
		/// </summary>
		/// <remarks>
		///     Retail answers every client <c>Close</c> with one: 11 for 11 across all seven captures that close a
		///     tradepost. It is also how retail ends the session when the player walks away from the auctioneer, dies
		///     or zones, none of which the client announces.
		/// </remarks>
		public static Task SendClose(INetworkClient client, uint interfaceInstance = DefaultInterfaceInstance)
		{
			return client.SendAsync(Build(TradepostInterfaceMethods.DispatchClose, [], interfaceInstance));
		}

		/// <summary>DispatchRetrieveMailResult (0x2004).</summary>
		public static Task SendRetrieveMailResult(INetworkClient client, uint interfaceInstance = DefaultInterfaceInstance)
		{
			return client.SendAsync(Build(TradepostInterfaceMethods.DispatchRetrieveMailResult, RetrieveMailAck, interfaceInstance));
		}

		/// <summary>
		///     Reply to TradepostItemSearch with database-backed listings for the searched item, cheapest first,
		///     echoing the inbound TradepostInterface instance so the client matches the reply to its window.
		/// </summary>
		/// <remarks>
		///     <para>
		///         The head word is the standard list frame over the row count, which is what the client sizes the grid
		///         from - so it must come from <see cref="ListFrame.ForCount" /> and never from the search category.
		///         The two coincide for a single-row result, which is why a category echo renders one row correctly and
		///         collapses every multi-row result to one.
		///     </para>
		///     <para>
		///         The per-row handles come straight off the stored listing rows. Retail's own ItemHandle is a shared
		///         per-item value and the seller handle repeats across rows, yet all rows render, because the client
		///         keys rows by saleId rather than by handle.
		///     </para>
		/// </remarks>
		public static Task SendSearchResults(INetworkClient client, uint interfaceInstance, IReadOnlyCollection<TradepostListing> listings)
		{
			var head = ListFrame.ForCount(listings.Count); // the list count, not a category echo

			return client.SendAsync(Build(TradepostInterfaceMethods.DispatchTradepostSearchResults, TradepostListingCodec.Build(head, listings), interfaceInstance));
		}

		/// <summary>
		///     Pushes one bank-slot stat update (0x201c on container 105):
		///     [kind=105][charInstance][slot][statId][value], byte-exact to the capture (F376/F377/F364/F398).
		/// </summary>
		/// <remarks>
		///     It rides the same GameCharAgent conversation as the bank slot registrations, so the client applies it to
		///     the live bank cell. The auction-house and mail overlays are its only callers, which is why it lives
		///     here.
		/// </remarks>
		public static Task SendBankSlotStat(INetworkClient client, uint slot, uint statId, uint value)
		{
			return client.SendAsync(new Rpc(Endpoints.Endpoint(RpcInterfaces.GameCharAgentType, 0x58), client.Endpoint(RpcInterfaces.GameCharInterfaceType), GameCharInterfaceMethods.DispatchSetItemStat,
				SetItemStatCodec.Build(SetItemStats.BankSlot(client.Session.Character.Id, slot, statId, value))));
		}

		private static Rpc Build(TradepostInterfaceMethods method, byte[] body, uint interfaceInstance)
		{
			var sender = new ProtoEndpoint
			{
				Type = RpcInterfaces.GameCharAgentType,
				Instance = GameCharAgentInstance
			};
			var receiver = new ProtoEndpoint
			{
				Type = RpcInterfaces.TradepostInterfaceType,
				Instance = interfaceInstance
			};

			return new Rpc(sender, receiver, (ushort)method, body);
		}
	}
}
