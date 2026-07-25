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

		/// <summary>s2c 0x206B (empty): acknowledges the recipient's OWN accept.</summary>
		Task SendAcceptAck(uint recipientId);

		/// <summary>s2c DispatchPartnerAcceptedTrade (0x206C): the other side accepted.</summary>
		Task SendPartnerAccepted(uint recipientId, uint partnerId);

		/// <summary>s2c 0x206D (empty) + the TradeCompletedSuccessfully overlay: the trade committed.</summary>
		Task SendTradeCompleted(uint recipientId);

		/// <summary>The wallet update after cash changes hands (the same 0x2005 the shop and loot paths send).</summary>
		Task SendWalletUpdate(uint characterId, ulong balanceTin);
	}
}
