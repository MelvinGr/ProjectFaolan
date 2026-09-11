using System.Threading.Tasks;
using Faolan.Core.Constants;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Inventory;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Codecs.Team;
using Faolan.Core.Network.Codecs.Text;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Crypt;
using Faolan.Core.Protobuf;
using Faolan.GameServer.Wire;

namespace Faolan.GameServer.Economy
{
	/// <summary>
	///     The two-player trade wire. It sits on <see cref="EconomyOutbound" />, because a trade ends in wallet updates
	///     and backpack grants, which are that layer's job.
	/// </summary>
	internal sealed class TradeOutbound : ITradeOutbound
	{
		private readonly ConnectionRegistry _connections;
		private readonly EconomyOutbound _economy;

		public TradeOutbound(ConnectionRegistry connections, EconomyOutbound economy)
		{
			_connections = connections;
			_economy = economy;
		}

		public Task SendTradeRequest(uint targetId, uint requesterId)
		{
			return SendTradeRpc(targetId, GameCharInterfaceMethods.DispatchRequestTrade, EntityIdCodec.Build(EntityIds.Character(requesterId)));
		}
		public Task SendTradeStarted(uint recipientId, uint partnerId)
		{
			return SendTradeRpc(recipientId, GameCharInterfaceMethods.DispatchStartTrade, EntityIdCodec.Build(EntityIds.Character(partnerId)));
		}
		public Task SendPartnerCash(uint recipientId, ulong tin)
		{
			return SendTradeRpc(recipientId, GameCharInterfaceMethods.DispatchPartnerUpdatedCash, TradeCashCodec.Build(new TradeCash(tin)));
		}
		/// <summary>
		///     One offered item. The same body either way; only the opcode says whose side of the table the item is on.
		/// </summary>
		/// <param name="offered">
		///     The offered instance, passed in rather than looked up: <paramref name="recipientId" /> is whoever is
		///     being told - the partner, for the <c>mine: false</c> half - and <paramref name="windowSlot" /> is not a
		///     bag slot, so neither can resolve it.
		/// </param>
		/// <param name="windowSlot">
		///     The trade-window position, not an inventory cell: it addresses the offer on both players' screens.
		/// </param>
		/// <remarks>
		///     The entry carries an ACG token, because every captured message describing a real item instance carries
		///     one - 165 of 165 DispatchAddItem entries, 8,479 of 8,479 snapshot entries, 2,734 of 2,734 shop wares -
		///     while only the template adverts (AH listings, mail attachments, claim lists) carry 0. A trade offer
		///     names an instance.
		/// </remarks>
		public async Task SendTradeItem(uint recipientId, ItemInstance offered, uint windowSlot, uint count, bool mine)
		{
			var item = offered.Template;
			var entry = SnapshotItems.InventoryEntry(FullCharacterRoots.ItemId(offered.Id), item, AcgHash.ForWire(item.Id, item.Hash),
			[
				new SnapshotItems.ItemStat(0, 1), new SnapshotItems.ItemStat(23, (uint)FullCharacterRoots.RecordId(recipientId, ContainerKind.Bag, windowSlot)),
				new SnapshotItems.ItemStat(26, count)
			], sockets: offered.Sockets);

			await SendTradeRpc(recipientId, mine
				? GameCharInterfaceMethods.DispatchTradeAddedItem
				: GameCharInterfaceMethods.DispatchTradePartnerAddedItem, TradeAddedItemCodec.Build(new TradeAddedItem(entry, count)));
		}
		/// <summary>
		///     The remove twin of <see cref="SendTradeItem" />: the same one-body, two-opcodes shape, so only the
		///     opcode says whose side of the table the item left.
		/// </summary>
		/// <remarks>
		///     Recovered from the client's method table rather than from a capture - see <c>TradeRemovedItemCodec</c>
		///     for why that is sound here.
		/// </remarks>
		public Task SendTradeItemRemoved(uint recipientId, uint slot, bool mine)
		{
			return SendTradeRpc(recipientId, mine
				? GameCharInterfaceMethods.DispatchTradeRemovedItem
				: GameCharInterfaceMethods.DispatchTradePartnerRemovedItem, TradeRemovedItemCodec.Build(slot));
		}
		public Task SendAcceptAck(uint recipientId)
		{
			// The client's handler is byte for byte DispatchPartnerAcceptedTrade's, on the local holder instead of the
			// one the ID32 names, so this is the argument-less "you" half - which is what the capture shows: it lands
			// on the accepter's own connection, empty, right after their AcceptTrade.
			return SendTradeRpc(recipientId, GameCharInterfaceMethods.DispatchClientAcceptedTrade, []);
		}
		public Task SendPartnerAccepted(uint recipientId, uint partnerId)
		{
			return SendTradeRpc(recipientId, GameCharInterfaceMethods.DispatchPartnerAcceptedTrade, EntityIdCodec.Build(EntityIds.Character(partnerId)));
		}
		/// <summary>
		///     The trade-completed method, then the toast - retail sends both, in that order, to each party.
		/// </summary>
		public async Task SendTradeCompleted(uint recipientId)
		{
			// The client's handler is CoreTradeHolder's own reset - the in-trade flag cleared, the accepted byte and
			// both cash slots zeroed, both item lists freed - and it fires the trade-ended signal with 1 where
			// TradeAbort (0x6A) sends 0. The capture agrees: it lands on both connections, empty, as the trade
			// commits.
			await SendTradeRpc(recipientId, GameCharInterfaceMethods.DispatchCompleteTrade, []);
			if (_connections.ForCharacter(recipientId) is { } client)
				await GameWire.SendFeedback(client, new FeedbackLiteral(FeedbackXml.TradeCompletedSuccessfully));
		}
		public Task SendWalletUpdate(uint characterId, ulong balanceTin)
		{
			return _connections.ForCharacter(characterId) is { } client
				? _economy.SendUpdateCash(client, balanceTin, UpdateCashReason.Spend) // a sync, not a celebrated gain - either side may be down money
				
				: Task.CompletedTask;
		}
		public Task SendItemRemoved(uint characterId, uint slot)
		{
			return _connections.ForCharacter(characterId) is { } client
				? EconomyOutbound.SendRemoveItem(client, ContainerKind.Bag, slot)
				: Task.CompletedTask;
		}
		public async Task<int?> GrantItemToCharacter(uint characterId, uint itemTemplateId, uint count)
		{
			// Resolving the connection here is the point of the character-id form: the trade path holds only the
			// receiver's id, and everything past this line is the shared grant - persistence, the live add, the
			// quest-container routing and the collect-goal hook.
			return _connections.ForCharacter(characterId) is { } client
				? await _economy.GrantItemToBackpack(client, itemTemplateId, count)
				: null;
		}
		/// <summary>
		///     The hand-over form: the same delivery, except the instance moves instead of a new one being minted, so
		///     the item keeps its identity, its crafter and its gems.
		/// </summary>
		public async Task<int?> TransferItemToCharacter(uint characterId, ItemInstance instance)
		{
			return _connections.ForCharacter(characterId) is { } client
				? await _economy.GrantItemToBackpack(client, instance.TemplateId, instance.Count, transferring: instance)
				: null;
		}
		/// <summary>
		///     One place the trade replies are addressed from, so the endpoints cannot drift between them.
		/// </summary>
		/// <remarks>
		///     Silently no-ops for a character who is not connected here: a partner who vanished mid-trade is
		///     <see cref="TradeService" />'s problem, not the wire's.
		/// </remarks>
		private Task SendTradeRpc(uint characterId, GameCharInterfaceMethods opcode, byte[] body)
		{
			return _connections.ForCharacter(characterId) is { } client
				? client.SendAsync(new Rpc(Endpoints.Endpoint(RpcInterfaces.GameCharAgentType, 0x10), client.Endpoint(RpcInterfaces.GameCharInterfaceType), opcode, body))
				: Task.CompletedTask;
		}
	}
}
