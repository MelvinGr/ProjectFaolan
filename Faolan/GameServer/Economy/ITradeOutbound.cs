using System.Threading.Tasks;
using Faolan.Core.Database.Tables;

namespace Faolan.GameServer.Economy
{
	// The wire side of a player trade, as a narrow port the listener implements - the same shape IEconomyOutbound takes
	// for the wallet/item-grant ops the shop, bank and tradepost share. TradeService owns the state machine and reaches
	// the wire only through this, so it never holds the listener (which is what the extraction in #200 removed).
	//
	// Every op is addressed by CHARACTER ID rather than a client, because a trade always involves two connections and
	// the service has no business resolving either.
	public interface ITradeOutbound
	{
		/// <summary>s2c DispatchRequestTrade (0x2068): tell <paramref name="targetId" /> that `requesterId` wants to trade.</summary>
		Task SendTradeRequest(uint targetId, uint requesterId);

		/// <summary>s2c DispatchStartTrade (0x2069): the window is open. Sent to each side carrying the OTHER party.</summary>
		Task SendTradeStarted(uint recipientId, uint partnerId);

		/// <summary>s2c DispatchPartnerUpdatedCash (0x206E): the partner changed what money is on the table.</summary>
		Task SendPartnerCash(uint recipientId, ulong tin);

		/// <summary>
		///     s2c DispatchTradeAddedItem (0x2070) when <paramref name="mine" />, else DispatchTradePartnerAddedItem
		///     (0x206F) - the same body, and which opcode depends only on whose side of the table the item is on.
		/// </summary>
		Task SendTradeItem(uint recipientId, Item item, uint slot, uint count, bool mine);

		/// <summary>
		///     s2c DispatchTradeRemovedItem (0x2074) when <paramref name="mine" />, else DispatchTradePartnerRemovedItem
		///     (0x2073) - the remove twin of <see cref="SendTradeItem" />, and the same rule: one body (the window slot),
		///     and which opcode depends only on whose side of the table the item left (#427).
		/// </summary>
		Task SendTradeItemRemoved(uint recipientId, uint slot, bool mine);

		/// <summary>s2c 0x206B (empty): acknowledges the recipient's OWN accept.</summary>
		Task SendAcceptAck(uint recipientId);

		/// <summary>s2c DispatchPartnerAcceptedTrade (0x206C): the other side accepted.</summary>
		Task SendPartnerAccepted(uint recipientId, uint partnerId);

		/// <summary>s2c 0x206D (empty) + the TradeCompletedSuccessfully overlay: the trade committed.</summary>
		Task SendTradeCompleted(uint recipientId);

		/// <summary>The wallet update after cash changes hands (the same 0x2005 the shop and loot paths send).</summary>
		Task SendWalletUpdate(uint characterId, ulong balanceTin);

		/// <summary>
		///     The GIVER's bag slot is now empty (DispatchRemoveItem 0x201F). Moving the row in the DB is not enough - the
		///     client keeps rendering what it last knew, so a traded-away item sits visibly in the giver's bag until relog.
		/// </summary>
		Task SendItemRemoved(uint characterId, uint slot);

		/// <summary>
		///     Grants an item to ANOTHER character - persist to their first free backpack cell and push the live
		///     DispatchAddItem - returning the slot, or null when they are offline or their bag is full.
		///     <para>
		///         This is the character-id form of <c>GrantItemToBackpack</c>, and it exists so the trade path goes
		///         through the SAME choke point as every other delivery. It used to be a send-only
		///         <c>SendItemGranted</c> with the caller writing the inventory row itself, which meant an item
		///         received in a trade never advanced a collect objective - the choke point is where
		///         <c>AdvanceCollectGoals</c> fires (#726, the sibling of #720).
		///     </para>
		///     <para>
		///         A character-id form rather than an <c>INetworkClient</c> one because in a trade the receiver is the
		///         OTHER player, whose connection this service does not hold - the same reason every other op on this
		///         port is keyed by character id.
		///     </para>
		/// </summary>
		Task<int?> GrantItemToCharacter(uint characterId, uint itemTemplateId, uint count);
	}
}
