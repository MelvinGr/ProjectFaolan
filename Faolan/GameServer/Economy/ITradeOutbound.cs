using System.Threading.Tasks;
using Faolan.Core.Database.Tables;

namespace Faolan.GameServer.Economy
{
	/// <summary>
	///     The wire side of a player trade, as a narrow port - the same shape <see cref="IEconomyOutbound" /> takes for
	///     the wallet and item-grant ops the shop, bank and tradepost share.
	/// </summary>
	/// <remarks>
	///     <see cref="TradeService" /> owns the state machine and reaches the wire only through this. Every op is
	///     addressed by character id rather than by client, because a trade always involves two connections and the
	///     service has no business resolving either.
	/// </remarks>
	public interface ITradeOutbound
	{
		/// <summary>s2c DispatchRequestTrade (0x2068): tell <paramref name="targetId" /> that `requesterId` wants to trade.</summary>
		Task SendTradeRequest(uint targetId, uint requesterId);

		/// <summary>s2c DispatchStartTrade (0x2069): the window is open. Sent to each side carrying the other party.</summary>
		Task SendTradeStarted(uint recipientId, uint partnerId);

		/// <summary>s2c DispatchPartnerUpdatedCash (0x206E): the partner changed what money is on the table.</summary>
		Task SendPartnerCash(uint recipientId, ulong tin);

		/// <summary>
		///     s2c DispatchTradeAddedItem (0x2070) when <paramref name="mine" />, else DispatchTradePartnerAddedItem
		///     (0x206F) - the same body, with the opcode depending only on whose side of the table the item is on.
		/// </summary>
		Task SendTradeItem(uint recipientId, ItemInstance offered, uint windowSlot, uint count, bool mine);

		/// <summary>
		///     s2c DispatchTradeRemovedItem (0x2074) when <paramref name="mine" />, else DispatchTradePartnerRemovedItem
		///     (0x2073) - the remove twin of <see cref="SendTradeItem" />, under the same rule: one body, the window
		///     slot, with the opcode depending only on whose side of the table the item left.
		/// </summary>
		Task SendTradeItemRemoved(uint recipientId, uint slot, bool mine);

		/// <summary>s2c 0x206B (empty): acknowledges the recipient's own accept.</summary>
		Task SendAcceptAck(uint recipientId);

		/// <summary>s2c DispatchPartnerAcceptedTrade (0x206C): the other side accepted.</summary>
		Task SendPartnerAccepted(uint recipientId, uint partnerId);

		/// <summary>s2c 0x206D (empty) + the TradeCompletedSuccessfully overlay: the trade committed.</summary>
		Task SendTradeCompleted(uint recipientId);

		/// <summary>The wallet update after cash changes hands (the same 0x2005 the shop and loot paths send).</summary>
		Task SendWalletUpdate(uint characterId, ulong balanceTin);

		/// <summary>
		///     The giver's bag slot is now empty (DispatchRemoveItem 0x201F). Moving the row in the DB is not enough - the
		///     client keeps rendering what it last knew, so a traded-away item sits visibly in the giver's bag until relog.
		/// </summary>
		Task SendItemRemoved(uint characterId, uint slot);

		/// <summary>
		///     Grants an item to another character - persist to their first free backpack cell and push the live
		///     DispatchAddItem - returning the slot, or null when they are offline or their bag is full.
		///     <para>
		///         This is the character-id form of <c>GrantItemToBackpack</c>, and it exists so the trade path goes
		///         through the same choke point as every other delivery - which is where <c>AdvanceCollectGoals</c>
		///         fires, so an item received in a trade advances a collect objective.
		///     </para>
		///     <para>
		///         A character-id form rather than an <c>INetworkClient</c> one because in a trade the receiver is the
		///         other player, whose connection this service does not hold - the same reason every other op on this
		///         port is keyed by character id.
		///     </para>
		/// </summary>
		Task<int?> GrantItemToCharacter(uint characterId, uint itemTemplateId, uint count);

		/// <summary>
		///     Hands an existing item to another character - the same first-free-cell delivery and live
		///     DispatchAddItem as <see cref="GrantItemToCharacter" />, except the instance moves rather than a fresh
		///     one being minted, so its crafter, its stack and its socketed gems come with it. Returns the slot, or
		///     null when they are offline or their bag is full, in which case the item has not moved.
		///     <para>
		///         A trade is the one delivery that is a hand-over rather than a creation. Re-minting would destroy
		///         per-instance state: the giver's row is deleted, cascading its sockets, and the receiver would get a
		///         gemless copy with no crafter.
		///     </para>
		/// </summary>
		Task<int?> TransferItemToCharacter(uint characterId, ItemInstance instance);
	}
}
