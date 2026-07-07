using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network;
using Faolan.Core.Network.Conan;
using Faolan.Core.Network.Packets;
using Faolan.GameServer.World;
using Microsoft.Extensions.Logging;
using Faolan.Core.Network.Codecs;
using Faolan.Core.Network.Opcodes;

namespace Faolan.GameServer.Economy.Shop
{
	// NPC vendor / shop flow (TODO §9b), decoded from the necrol80 sell_items_to_trader capture:
	//   DL 0x2033 DispatchShopData   [shop ID32 (type 0xC75B)][vendor NPC ID32][protobuf shop content]
	//   UL 0x2034 SellItem           [shop ID32][u32 container][u32 charId][u32 slot]
	//   DL 0x2035 DispatchItemSoldToShop [u32 container][u32 charId][u32 slot]  (the client clears the bag slot)
	//   DL 0x2005 money update (the wallet credit - via the injected money-update seam)
	//   UL 0x2033 BuyItem            [shop ID32][u32 pos]
	//   UL 0x2032 CloseShop, UL 0x2035 UndoLastSoldItem
	// The wire container 0x68 (104) IS the DB CharacterInventory.Container bag value - no mapping needed.
	// Extracted out of GameServerListener as a composed collaborator: it owns the open-shop/last-sale session
	// state and the sell/buy/undo logic. The two cross-cutting OUTBOUND ops it needs - the wallet update and the
	// live item grant - are reached through the IEconomyOutbound port (the listener implements it; the ops stay
	// on the listener because they're shared by many subsystems).
	public sealed class ShopService
	{
		// Vendor BUY price in tin (what the player pays): a flat markup over the SAME item's sell value so wares
		// can't be flipped for free (buy > sell). Grounded in the real value when known; the markup itself has no
		// captured anchor and stays the open part (#137). A value-less ware still costs at least the markup (min 1
		// per-unit) so the shop never gives it away free.
		public const uint VendorBuyMarkup = 4;

		// Session-volatile shop instance ids we hand the client in DispatchShopData (the value the BuyItem echoes
		// back). Uniqueness only needs to hold within a session; a process-wide counter is plenty.
		private static int _shopInstanceSeq;

		private readonly ConcurrentDictionary<INetworkClient, LastShopSale> _lastShopSale = new ConcurrentDictionary<INetworkClient, LastShopSale>();

		private readonly ConcurrentDictionary<INetworkClient, OpenShop> _openShop = new ConcurrentDictionary<INetworkClient, OpenShop>();
		private readonly IDatabaseRepository _database;
		private readonly IEconomyOutbound _outbound;
		private readonly ILogger _logger;

		public ShopService(IDatabaseRepository database, ILogger logger, IEconomyOutbound outbound)
		{
			_database = database;
			_logger = logger;
			_outbound = outbound;
		}

		// UL 0x2033 BuyItem [shop ID32 type][shop instance][slot]: resolve the clicked slot against the open shop,
		// price it, debit the wallet (DispatchUpdateCash) and grant the item live (DispatchAddItem via
		// GrantItemToBackpack). Refuses on no-open-shop, unknown slot, insufficient funds or a full bag.
		public async Task HandleBuyItemFromShop(INetworkClient client, uint shopInstance, uint pos)
		{
			var character = client.Session.Character;
			if (character == null)
				return;

			if (!_openShop.TryGetValue(client, out var shop) || shop.Instance != shopInstance)
			{
				_logger.LogWarning("BuyItem: no open shop (or stale instance 0x{Instance:X}) for {Character}.", shopInstance, character.Name);
				return;
			}

			if (!shop.WaresByPos.TryGetValue(pos, out var ware))
			{
				_logger.LogWarning("BuyItem: shop \"{Vendor}\" has no ware at slot {Pos} for {Character}.", shop.VendorName, pos, character.Name);
				return;
			}

			var wareItem = await _database.GetItem(ware.ItemTemplateId);
			var price = VendorBuyPriceTin(wareItem?.Value ?? 0, ware.Count);
			if (character.Money < price)
			{
				_logger.LogInformation("BuyItem: {Character} can't afford ware {Template} at slot {Pos} ({Price} tin, has {Balance}).", character.Name, ware.ItemTemplateId, pos, price, character.Money);
				return;
			}

			// Grant first so a full bag (null slot) aborts the purchase before any money moves.
			var slot = await _outbound.GrantItemToBackpack(client, ware.ItemTemplateId, ware.Count);
			if (slot == null)
			{
				_logger.LogWarning("BuyItem: couldn't grant ware {Template} to {Character} (bag full or item not buildable); purchase aborted.", ware.ItemTemplateId, character.Name);
				return;
			}

			character.Money -= price;
			await _database.SetCharacterMoney(character.Id, character.Money);
			await _outbound.SendUpdateCash(client, character.Money, UpdateCashReason.Spend);

			_logger.LogInformation("Shop: {Character} bought ware {Template} x{Count} (slot {Pos}) from \"{Vendor}\" for {Price} tin into bag slot {BagSlot} (wallet {Balance}).",
				character.Name,
				ware.ItemTemplateId,
				ware.Count,
				pos,
				shop.VendorName,
				price,
				slot,
				character.Money);
		}

		// UL 0x2034 SellItem: validate the bag slot, price the item, remove it, credit the wallet, and confirm
		// with DispatchItemSoldToShop + the 0x2005 balance update (retail's exact reply trio minus the 0x201C
		// record-stream entry, which is bookkeeping we don't model).
		public async Task HandleSellItemToShop(INetworkClient client, ConanPacket packet)
		{
			packet.Body.ReadUInt32(); // shop ID32 type (0xC75B)
			var shopId = packet.Body.ReadUInt32();
			var container = packet.Body.ReadUInt32();
			var sellerId = packet.Body.ReadUInt32();
			var slot = packet.Body.ReadUInt32();

			var character = client.Session.Character;
			if (character == null)
				return;

			var row = (await _database.GetCharacterInventory(character.Id)).FirstOrDefault(i => i.Container == (int)container && i.Slot == (int)slot);
			if (row?.Item == null)
			{
				_logger.LogWarning("SellItem to shop {Shop:X}: no item in container {Container} slot {Slot} for {Character}.", shopId, container, slot, character.Name);
				return;
			}

			var price = VendorPriceTin(row.Item.Value, row.Count);
			if (price == 0)
			{
				// A valueless item (RDB value 0, e.g. the Totem of Origins) is no-sell - refuse rather than invent
				// a price. The client keeps the item in the slot since we send no DispatchItemSoldToShop.
				_logger.LogInformation("SellItem: {Item} (template {Template}) is valueless / not sellable for {Character}.", row.Item.Name, row.Item.Id, character.Name);
				return;
			}

			await _database.SetCharacterInventorySlot(character.Id, (int)container, (int)slot, null);
			character.Money += price;
			await _database.SetCharacterMoney(character.Id, character.Money);
			_lastShopSale[client] = new LastShopSale((int)container, (int)slot, row.ItemId ?? 0, row.Count, price);

			var ps = new PacketStream();
			ps.WriteHeader(packet.Receiver, packet.Sender, GameCharInterfaceDispatchOpcodes.DispatchItemSoldToShop);
			WriteItemSoldBody(ps, container, sellerId, slot);
			await ps.SendAsync(client);
			await _outbound.SendUpdateCash(client, character.Money, UpdateCashReason.Gain);

			_logger.LogInformation("Shop: {Character} sold \"{Item}\" x{Count} (container {Container} slot {Slot}) for {Price} tin (wallet {Balance}).",
				character.Name,
				row.Item.Name,
				row.Count,
				container,
				slot,
				price,
				character.Money);
		}

		// UL 0x2035 UndoLastSoldItem: restore the last sale (item back to its slot, price debited).
		public async Task HandleUndoLastSoldItem(INetworkClient client)
		{
			var character = client.Session.Character;
			if (character == null || !_lastShopSale.TryRemove(client, out var sale) || sale.ItemId == 0)
			{
				_logger.LogInformation("UndoLastSoldItem: nothing to undo.");
				return;
			}

			await _database.SetCharacterInventorySlot(character.Id, sale.Container, sale.Slot, sale.ItemId, sale.Count);
			character.Money = character.Money >= sale.PriceTin
				? character.Money - sale.PriceTin
				: 0;
			await _database.SetCharacterMoney(character.Id, character.Money);
			await _outbound.SendUpdateCash(client, character.Money, UpdateCashReason.Spend);

			_logger.LogInformation("Shop: {Character} undid the sale of item {ItemId} (slot {Slot}); {Price} tin debited (wallet {Balance}).", character.Name, sale.ItemId, sale.Slot, sale.PriceTin, character.Money);
		}

		// The real shop-OPEN trigger (#134): the client clicked a vendor NPC (a TalkToNpc BCC Interact whose
		// SpellTarget is the vendor handle), and the shop window is gated on the reply riding THAT conversation -
		// a cold DispatchShopData never pops it. Retail's reply sequence (sell_items_to_trader frames 1189/1193,
		// see ShopOpenBuilder): the 0x0D155695 trade-select echo, the bare (unpadded) BCC echo-ack, then the shop
		// content. `rawPayload` is the inbound Interact exactly as received (the echo re-frames it).
		public async Task HandleVendorTradeOpen(INetworkClient client, byte[] rawPayload, uint mode, Core.Database.Tables.Shop shop, uint npcHandle)
		{
			var instance = client.Session.Account.ClientInstance;
			await SendWorldPacket(client, ShopOpenBuilder.BuildTradeSelectEcho(instance, npcHandle));
			await SendWorldPacket(client, BehaviorAckBody.BuildBehaviorEchoAckBody(rawPayload, instance, mode, padTail: false));
			await SendShopData(client, shop, npcHandle);

			_logger.LogInformation("Shop: vendor trade-open for \"{Vendor}\" (npc 0x{Handle:X8}) by {Character}.", shop.VendorName, npcHandle, client.Session.Character?.Name);
		}

		// DL 0x2033 DispatchShopData: serve a vendor's window and remember it for this session so BuyItem can
		// resolve a clicked slot. `npcHandle` is the vendor's world-entity handle (the client renders the window
		// against it). NB: the window only OPENS in-client off the client's own NPC trade-initiation (the shop is
		// KnubotStartTrade-gated, same family as the mid-session-spawn blocker); a cold push doesn't pop the
		// window. This sets up the server-side state so the BUY round-trip is complete and exercisable.
		public async Task SendShopData(INetworkClient client, Core.Database.Tables.Shop shop, uint npcHandle)
		{
			// The codec's item resolver is synchronous; pre-resolve each ware's Items row up front.
			var resolved = new Dictionary<uint, Item>();
			foreach (var ware in shop.Items)
			{
				if (!resolved.ContainsKey(ware.ItemTemplateId))
					resolved[ware.ItemTemplateId] = await _database.GetItem(ware.ItemTemplateId);
			}

			var instance = (uint)(0x00010000 + (Interlocked.Increment(ref _shopInstanceSeq) & 0xFFFF));
			var body = ShopContentCodec.Build(instance, npcHandle, shop.MaxSize, shop.Items, id => resolved.GetValueOrDefault(id));

			// Track only the wares that actually resolved (the codec skips the rest, so their slots aren't on the wire).
			var waresByPos = shop.Items.Where(w => resolved.GetValueOrDefault(w.ItemTemplateId) != null).ToDictionary(w => w.Pos);
			_openShop[client] = new OpenShop(instance, shop.VendorName, waresByPos);

			var ps = new PacketStream();
			ps.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharAgentType, 0x58),
				ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharInterfaceType, client.Session.Account.ClientInstance),
				GameCharInterfaceDispatchOpcodes.DispatchShopData);
			ps.WriteArray(body);
			await ps.SendAsync(client);

			_logger.LogInformation("Shop: served \"{Vendor}\" (instance 0x{Instance:X}, {Count} wares) to {Character}.", shop.VendorName, instance, waresByPos.Count, client.Session.Character?.Name);
		}

		public static uint VendorBuyPriceTin(uint value, uint count)
		{
			return Math.Max(1u, PerUnitValueTin(value)) * Math.Max(1u, count) * VendorBuyMarkup;
		}

		// Vendor SELL price in tin (what the player gets selling to the vendor) = round(value/10) per unit. Returns 0
		// for a valueless item (Item.Value == 0) - HandleSellItemToShop refuses to sell those (they are no-value /
		// no-sell, like the Totem of Origins), rather than inventing a level-based price.
		public static uint VendorPriceTin(uint value, uint count)
		{
			return PerUnitValueTin(value) * Math.Max(1u, count);
		}

		// DL 0x2035 - tells the client the bag slot was sold (it clears the slot itself).
		// Body golden-tested against the captured slot-5 sale (ShopTests).
		public static void WriteItemSoldBody(FaolanStream s, uint container, uint characterId, uint slot)
		{
			s.WriteUInt32(container).WriteUInt32(characterId).WriteUInt32(slot);
		}

		// The per-unit value in tin (the wallet wire unit) = round(Item.Value / 10), where Item.Value is the RDB
		// item-template's field 74. Proven by the captured sales (190->19, 920->92, 560->56 tin; #137). Value 0 is a
		// genuinely valueless item (the template carries field 74 == 0, e.g. the unsellable Totem of Origins) -> 0,
		// NOT a price; SELL treats that as unsellable and BUY adds its markup on top.
		private static uint PerUnitValueTin(uint value)
		{
			return value > 0
				? Math.Max(1u, (value + 5) / 10)
				: 0;
		}

		// The world-conversation IIR carrier (same framing every world IIR uses; cf. CombatService.SendWorldPacket).
		private static Task SendWorldPacket(INetworkClient client, byte[] body)
		{
			return new PacketStream().WriteHeader(WorldFraming.Sender, WorldFraming.Receiver(client), GameInterfaceDispatchOpcodes.DispatchPassBlob).WriteArrayUInt32Length(s => s.WriteArray(body)).SendAsync(client);
		}

		// The last sale per connection, so UndoLastSoldItem can restore it (retail's undo is single-step too).
		private sealed record LastShopSale(int Container, int Slot, uint ItemId, uint Count, uint PriceTin);

		// The shop window the client currently has open, recorded when we send DispatchShopData. BuyItem only
		// carries the session-volatile shop instance + a slot position, so we need the open window's wares to
		// resolve the position back to a template id and price it. Keyed by Pos (the shop-window slot).
		private sealed record OpenShop(uint Instance, string VendorName, IReadOnlyDictionary<uint, ShopItem> WaresByPos);
	}
}
