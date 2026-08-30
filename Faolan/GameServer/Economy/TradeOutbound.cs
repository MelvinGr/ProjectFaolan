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

namespace Faolan.GameServer.Economy
{
	/// <summary>
	///     The two-player TRADE wire (#826), off GameServerListener. Sits ON <see cref="EconomyOutbound" />: a trade
	///     ends in wallet updates and backpack grants, which are that layer's job, so this holds it rather than
	///     keeping a second copy.
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

		// --- ITradeOutbound: the wire side of a player trade. TradeService owns the state machine and reaches the wire
		// only through this port, addressed by character id because a trade always spans two connections. ---------------
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
		// The same body either way; only the opcode says whose side of the table the item is on.
		// The token was a bare 0 here. No capture pins that (the trade path was recovered from the client's method
		// table, not a trace) and it contradicts the measured rule: every captured message describing a real item
		// INSTANCE carries one - 165/165 DispatchAddItem entries, 8,479/8,479 snapshot entries, 2,734/2,734 shop
		// wares - while only the template ADVERTS (AH listings, mail attachments, claim lists) carry 0. A trade
		// offer names an instance, so it carries the token (#723).
		// `windowSlot` is the TRADE WINDOW position, not an inventory cell - it addresses the offer on both players'
		// screens. The offered INSTANCE is passed in rather than looked up, because neither of the two things this
		// method knows can find it: `recipientId` is whoever is being TOLD (the partner, for the `mine: false` half),
		// and `windowSlot` is not a bag slot. Resolving it from those two was wrong in both directions.
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
		// The remove twin of SendTradeItem: the same one-body/two-opcodes shape, so only the opcode says whose side of
		// the table the item left. Recovered from the client's method table rather than a capture - see
		// TradeRemovedItemCodec for why that is sound here (#427).
		public Task SendTradeItemRemoved(uint recipientId, uint slot, bool mine)
		{
			return SendTradeRpc(recipientId, mine
				? GameCharInterfaceMethods.DispatchTradeRemovedItem
				: GameCharInterfaceMethods.DispatchTradePartnerRemovedItem, TradeRemovedItemCodec.Build(slot));
		}
		public Task SendAcceptAck(uint recipientId)
		{
			// #792 named it: its handler is byte for byte DispatchPartnerAcceptedTrade's, on the LOCAL holder instead
			// of the one the ID32 names - so it is the argument-less YOU half. Which is exactly what the capture
			// already said: it lands on the accepter's own connection, empty, right after their AcceptTrade.
			return SendTradeRpc(recipientId, GameCharInterfaceMethods.DispatchClientAcceptedTrade, []);
		}
		public Task SendPartnerAccepted(uint recipientId, uint partnerId)
		{
			return SendTradeRpc(recipientId, GameCharInterfaceMethods.DispatchPartnerAcceptedTrade, EntityIdCodec.Build(EntityIds.Character(partnerId)));
		}
		// The 0x6D trade-completed method then the toast - retail sends both, in that order, to each party.
		public async Task SendTradeCompleted(uint recipientId)
		{
			// #792 named it too: the client's handler is CoreTradeHolder's own reset - the in-trade flag cleared, the
			// accepted byte and both cash slots zeroed, both item lists freed - and it fires the trade-ended signal
			// with 1 where TradeAbort (0x6A) sends 0. The capture agrees: it lands on BOTH connections, empty, as the
			// trade commits.
			await SendTradeRpc(recipientId, GameCharInterfaceMethods.DispatchCompleteTrade, []);
			if (_connections.ForCharacter(recipientId) is { } client)
				await GameServerListener.SendFeedback(client, new FeedbackLiteral(FeedbackXml.TradeCompletedSuccessfully));
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
			// Resolving the connection here is the whole point of the character-id form: the trade path holds only
			// the receiver's id, and everything past this line is the shared grant - persistence, the live add, the
			// quest-container routing and AdvanceCollectGoals (#726).
			return _connections.ForCharacter(characterId) is { } client
				? await _economy.GrantItemToBackpack(client, itemTemplateId, count)
				: null;
		}
		// The HAND-OVER form: the same delivery, except `transferring` MOVES instead of a new instance being minted, so
		// the item keeps its identity, its crafter and its gems (#750).
		public async Task<int?> TransferItemToCharacter(uint characterId, ItemInstance instance)
		{
			return _connections.ForCharacter(characterId) is { } client
				? await _economy.GrantItemToBackpack(client, instance.TemplateId, instance.Count, transferring: instance)
				: null;
		}
		// One place the trade replies are addressed from, so the endpoints cannot drift between them. Silently no-ops for
		// a character who is not connected here - a partner who vanished mid-trade is TradeService's problem, not the
		// wire's.
		private Task SendTradeRpc(uint characterId, GameCharInterfaceMethods opcode, byte[] body)
		{
			return _connections.ForCharacter(characterId) is { } client
				? client.SendAsync(new Rpc(Endpoints.Endpoint(RpcInterfaces.GameCharAgentType, 0x10), client.Endpoint(RpcInterfaces.GameCharInterfaceType), opcode, body))
				: Task.CompletedTask;
		}
	}
}
