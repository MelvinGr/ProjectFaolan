using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Constants;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Progression;
using Faolan.GameServer.Wire;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Economy
{
	/// <summary>
	///     The NPC vendor and shop flow: the open-shop and last-sale session state, and the sell, buy and undo logic.
	/// </summary>
	/// <remarks>
	///     <para>
	///         Decoded from the NecroL80 sell_items_to_trader capture:
	///     </para>
	///     <code>
	///     DL 0x2033 DispatchShopData        [shop ID32 (type 0xC75B)][vendor NPC ID32][protobuf shop content]
	///     UL 0x2034 SellItem                [shop ID32][u32 container][u32 charId][u32 slot]
	///     DL 0x2035 DispatchItemSoldToShop  [u32 container][u32 charId][u32 slot]  (the client clears the bag slot)
	///     DL 0x2005 money update            the wallet credit
	///     UL 0x2033 BuyItem                 [shop ID32][u32 pos]
	///     UL 0x2032 CloseShop, UL 0x2035 UndoLastSoldItem
	///     </code>
	///     <para>
	///         The wire container 0x68 (104) is the database's own bag container value, so there is no mapping. The
	///         two cross-cutting outbound ops - the wallet update and the live item grant - are reached through the
	///         <see cref="IEconomyOutbound" /> port, since they are shared by many subsystems.
	///     </para>
	/// </remarks>
	public sealed class ShopService
	{
		// The vendor buy markup: a flat multiple over the same item's sell value, so wares cannot be flipped for free.
		// The sell value is grounded in the item's real value; the markup itself has no captured anchor. A valueless
		// ware still costs at least the markup, at a minimum of 1 per unit, so the shop never gives it away.
		public const uint VendorBuyMarkup = 4;

		// Session-volatile shop instance ids handed to the client in DispatchShopData, and echoed back by BuyItem.
		// Uniqueness only needs to hold within a session, so a process-wide counter is enough.
		private static int _shopInstanceSeq;

		private readonly ConcurrentDictionary<INetworkClient, LastShopSale> _lastShopSale = new ConcurrentDictionary<INetworkClient, LastShopSale>();

		private readonly ConcurrentDictionary<INetworkClient, OpenShop> _openShop = new ConcurrentDictionary<INetworkClient, OpenShop>();
		private readonly ICatalogRepository _catalog;
		private readonly ICharacterInventoryRepository _inventory;
		private readonly IEconomyOutbound _outbound;
		private readonly ILogger _logger;

		public ShopService(ICatalogRepository catalog, ICharacterInventoryRepository inventory, ILogger logger, IEconomyOutbound outbound)
		{
			_catalog = catalog;
			_inventory = inventory;
			_logger = logger;
			_outbound = outbound;
		}

		/// <summary>
		///     UL 0x2033 BuyItem [shop ID32 type][shop instance][slot]: resolves the clicked slot against the open
		///     shop, prices it, debits the wallet (DispatchUpdateCash) and grants the item live (DispatchAddItem).
		/// </summary>
		/// <remarks>
		///     Refuses on no open shop, an unknown slot, insufficient funds or a full bag.
		/// </remarks>
		public async Task HandleBuyItemFromShop(INetworkClient client, uint shopInstance, uint pos)
		{
			var character = client.Session.Character;

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

			// The trade-open gate already refuses a criminal, so reaching here means the player went criminal with the
			// window already open. Re-checked rather than assumed: the open shop is remembered per session, so without
			// this a buy would still go through on a stale window.
			if (character.IsCriminal && !CriminalStatus.TradesWithCriminals(shop.VendorName))
			{
				_logger.LogInformation("BuyItem: \"{Vendor}\" refuses {Character} - criminal until {Until:u} (#503).", shop.VendorName, character.Name, character.CriminalUntil);
				return;
			}

			// No `?? 0` on the price: a ware pointing at an absent item row is a broken shop, and pricing it at zero
			// would sell it for free. The grant below fails on the same missing row, so the purchase aborts either way,
			// but relying on that would leave a free-item exploit one refactor away.
			var wareItem = await _catalog.GetItem(ware.ItemTemplateId);
			if (wareItem == null)
			{
				_logger.LogError("BuyItem: shop \"{Vendor}\" slot {Pos} sells item {Template}, which has no Items row - the shop's ware list needs re-importing.", shop.VendorName, pos,
					ware.ItemTemplateId);
				return;
			}

			var price = VendorBuyPriceTin(wareItem.Value, ware.Count);
			if (character.Money < price)
			{
				_logger.LogInformation("BuyItem: {Character} can't afford ware {Template} at slot {Pos} ({Price} tin, has {Balance}).", character.Name, ware.ItemTemplateId, pos, price,
					character.Money);
				return;
			}

			// Grant first, so a full bag - a null slot - aborts the purchase before any money moves.
			var slot = await _outbound.GrantItemToBackpack(client, ware.ItemTemplateId, ware.Count);
			if (slot == null)
			{
				_logger.LogWarning("BuyItem: couldn't grant ware {Template} to {Character} (bag full or item not buildable); purchase aborted.", ware.ItemTemplateId, character.Name);
				return;
			}

			character.Money -= price;
			await _inventory.SetCharacterMoney(character.Id, character.Money);
			await _outbound.SendUpdateCash(client, character.Money, UpdateCashReason.Spend);

			_logger.LogInformation("Shop: {Character} bought ware {Template} x{Count} (slot {Pos}) from \"{Vendor}\" for {Price} tin into bag slot {BagSlot} (wallet {Balance}).", character.Name,
				ware.ItemTemplateId, ware.Count, pos, shop.VendorName, price, slot, character.Money);
		}

		/// <summary>
		///     UL 0x2034 SellItem: validates the bag slot, prices the item, removes it, credits the wallet and confirms
		///     with DispatchItemSoldToShop plus the 0x2005 balance update.
		/// </summary>
		/// <remarks>
		///     That is retail's reply trio minus the 0x201C record-stream entry, which is bookkeeping this server does
		///     not model.
		/// </remarks>
		public async Task HandleSellItemToShop(INetworkClient client, Rpc packet, FaolanStream body)
		{
			body.ReadUInt32(); // shop ID32 type (0xC75B)
			var shopId = body.ReadUInt32();
			var container = body.ReadUInt32();
			var sellerId = body.ReadUInt32();
			var slot = body.ReadUInt32();

			var character = client.Session.Character;

			// "Normal vendors will not trade with you" cuts both ways: a criminal cannot sell either. The open shop is
			// per-session, so like BuyItem this must be re-checked rather than left to the trade-open gate.
			if (character.IsCriminal && _openShop.TryGetValue(client, out var openShop))
				if (!CriminalStatus.TradesWithCriminals(openShop.VendorName))
				{
					_logger.LogInformation("SellItem: \"{Vendor}\" refuses {Character} - criminal until {Until:u} (#503).", openShop.VendorName, character.Name, character.CriminalUntil);
					return;
				}

			var sold = await _inventory.GetCharacterItemSlot(character.Id, (ContainerKind)container, (int)slot);
			if (sold == null)
			{
				_logger.LogWarning("SellItem to shop {Shop:X}: no item in container {Container} slot {Slot} for {Character}.", shopId, container, slot, character.Name);
				return;
			}

			var price = VendorPriceTin(sold.Template.Value, sold.Count);
			if (price == 0)
			{
				// A valueless item (RDB value 0, such as the Totem of Origins) is no-sell, so refuse rather than
				// invent a price. The client keeps the item in the slot, because no DispatchItemSoldToShop is sent.
				_logger.LogInformation("SellItem: {Item} (template {Template}) is valueless / not sellable for {Character}.", sold.Template.Name, sold.TemplateId, character.Name);
				return;
			}

			await _inventory.SetCharacterItemSlot(character.Id, (ContainerKind)container, (int)slot, null);
			character.Money += price;
			await _inventory.SetCharacterMoney(character.Id, character.Money);
			// The undo re-mints the item from its template, so anything per-instance about the sold one is gone: a
			// crafted item comes back without its crafter and a gemmed one without its gems. The sold instance is
			// deleted outright rather than parked, which is what makes that unavoidable; an undo buffer would have to
			// move it to a holding container instead of deleting it.
			_lastShopSale[client] = new LastShopSale((int)container, (int)slot, sold.TemplateId, sold.Count, price);

			await client.SendAsync(new Rpc(packet.Receiver, packet.Sender, GameCharInterfaceMethods.DispatchItemSoldToShop, b => WriteItemSoldBody(b, container, sellerId, slot)));
			await _outbound.SendUpdateCash(client, character.Money, UpdateCashReason.Gain);

			_logger.LogInformation("Shop: {Character} sold \"{Item}\" x{Count} (container {Container} slot {Slot}) for {Price} tin (wallet {Balance}).", character.Name, sold.Template.Name, sold.Count,
				container, slot, price, character.Money);
		}

		/// <summary>
		///     UL 0x2035 UndoLastSoldItem: restores the last sale - the item back to its slot, the price debited.
		/// </summary>
		public async Task HandleUndoLastSoldItem(INetworkClient client)
		{
			var character = client.Session.Character;
			if (character == null || !_lastShopSale.TryRemove(client, out var sale) || sale.ItemId == 0)
			{
				_logger.LogInformation("UndoLastSoldItem: nothing to undo.");
				return;
			}

			if (await _inventory.SetCharacterItemSlot(character.Id, (ContainerKind)sale.Container, sale.Slot, sale.ItemId, sale.Count) == null)
			{
				_logger.LogInformation("UndoLastSoldItem: {Character}'s slot {Slot} is occupied again; nothing restored, nothing debited.", character.Name, sale.Slot);
				return;
			}

			character.Money = character.Money >= sale.PriceTin
				? character.Money - sale.PriceTin
				: 0;
			await _inventory.SetCharacterMoney(character.Id, character.Money);
			await _outbound.SendUpdateCash(client, character.Money, UpdateCashReason.Spend);

			_logger.LogInformation("Shop: {Character} undid the sale of item {ItemId} (slot {Slot}); {Price} tin debited (wallet {Balance}).", character.Name, sale.ItemId, sale.Slot, sale.PriceTin,
				character.Money);
		}

		/// <summary>
		///     The shop-open trigger: the client clicked a vendor NPC - a TalkToNpc BCC Interact whose target is the
		///     vendor handle - and the shop window is gated on the reply riding that conversation, so a cold
		///     DispatchShopData never pops it.
		/// </summary>
		/// <param name="rawPayload">The inbound Interact exactly as received; the echo re-frames it.</param>
		/// <remarks>
		///     Retail's reply sequence (sell_items_to_trader frames 1189/1193) is the 0x0D155695 trade-select echo, the
		///     bare unpadded BCC echo-ack, then the shop content.
		/// </remarks>
		public async Task HandleVendorTradeOpen(INetworkClient client, byte[] rawPayload, uint mode, Shop shop, uint npcHandle)
		{
			// A criminal is refused by normal vendors, as the capture's annotations record. It is retail's rule, not
			// this server's: the captured "Criminal" spell's own description says "normal vendors will not trade with
			// you... you can seek out smugglers at outlaw camps instead", so the smuggler exception is part of the same
			// sentence.
			//
			// Refused at the trade-open rather than at the buy, because the NPC does not deal at all. No refusal
			// message is sent, because none is decoded - retail evidently shows the player something, but the capture's
			// refusal was on a travel NPC and the packet is unidentified. Logged so a silent no-op is still explicable
			// from the server side.
			if (client.Session.Character.IsCriminal && !CriminalStatus.TradesWithCriminals(shop.VendorName))
			{
				_logger.LogInformation("Shop: \"{Vendor}\" refuses {Character} - criminal until {Until:u} (#503).",
					shop.VendorName, client.Session.Character.Name, client.Session.Character.CriminalUntil);
				return;
			}

			var instance = client.Session.Character.Id;
			await SendWorldPacket(client, LookAtCodec.Build(LookAts.Select(instance, npcHandle)));
			await SendWorldPacket(client, BehaviorControlCenterCodec.Build(BehaviorAcks.EchoAck(rawPayload, instance, mode)));
			await SendShopData(client, shop, npcHandle);

			_logger.LogInformation("Shop: vendor trade-open for \"{Vendor}\" (npc 0x{Handle:X8}) by {Character}.", shop.VendorName, npcHandle, client.Session.Character?.Name);
		}

		/// <summary>
		///     DL 0x2033 DispatchShopData: serves a vendor's window and remembers it for this session so BuyItem can
		///     resolve a clicked slot.
		/// </summary>
		/// <param name="npcHandle">The vendor's world-entity handle; the client renders the window against it.</param>
		/// <remarks>
		///     The window only opens in-client off the client's own NPC trade-initiation, because the shop is
		///     KnubotStartTrade-gated - a cold push does not pop it. This sets up the server-side state so the buy
		///     round-trip is complete and exercisable.
		/// </remarks>
		public async Task SendShopData(INetworkClient client, Shop shop, uint npcHandle)
		{
			// The codec's item resolver is synchronous, so every ware's Items row is pre-resolved in one read rather
			// than a query per ware - a stocked vendor is dozens of them, paid on every window open.
			var resolved = (await _catalog.GetItems([.. shop.Items.Select(w => w.ItemTemplateId).Distinct()])).ToDictionary(i => i.Id);

			var instance = (uint)(0x00010000 + (Interlocked.Increment(ref _shopInstanceSeq) & 0xFFFF));
			var body = ShopContentCodec.Build(instance, npcHandle, shop.MaxSize, shop.Items, id => resolved.GetValueOrDefault(id)!);

			// Track only the wares that resolved: the codec skips the rest, so their slots are not on the wire.
			var waresByPos = shop.Items.Where(w => resolved.GetValueOrDefault(w.ItemTemplateId) != null).ToDictionary(w => w.Pos);
			_openShop[client] = new OpenShop(instance, shop.VendorName, waresByPos);

			await client.SendAsync(new Rpc(Endpoints.Endpoint(RpcInterfaces.GameCharAgentType, 0x58), client.Endpoint(RpcInterfaces.GameCharInterfaceType), GameCharInterfaceMethods.DispatchShopData, body));

			_logger.LogInformation("Shop: served \"{Vendor}\" (instance 0x{Instance:X}, {Count} wares) to {Character}.", shop.VendorName, instance, waresByPos.Count, client.Session.Character?.Name);
		}

		public static uint VendorBuyPriceTin(uint value, uint count)
		{
			return Math.Max(1u, PerUnitValueTin(value)) * Math.Max(1u, count) * VendorBuyMarkup;
		}

		/// <summary>
		///     The vendor sell price in tin - what the player gets selling to the vendor - which is round(value / 10)
		///     per unit.
		/// </summary>
		/// <returns>
		///     0 for a valueless item (<c>Item.Value == 0</c>). <see cref="HandleSellItemToShop" /> refuses to sell
		///     those rather than inventing a level-based price.
		/// </returns>
		public static uint VendorPriceTin(uint value, uint count)
		{
			return PerUnitValueTin(value) * Math.Max(1u, count);
		}

		/// <summary>
		///     DL 0x2035 - tells the client the bag slot was sold; it clears the slot itself. The body is
		///     golden-tested against the captured slot-5 sale.
		/// </summary>
		public static void WriteItemSoldBody(FaolanStream s, uint container, uint characterId, uint slot)
		{
			s.WriteUInt32(container).WriteUInt32(characterId).WriteUInt32(slot);
		}

		// The per-unit value in tin, the wallet's wire unit: round(Item.Value / 10), where Item.Value is the RDB item
		// template's field 74. Proven by the captured sales (190 to 19, 920 to 92, 560 to 56 tin). Value 0 is a
		// genuinely valueless item - the template carries field 74 == 0, as the unsellable Totem of Origins does - and
		// is not a price: sell treats it as unsellable, and buy adds its markup on top.
		private static uint PerUnitValueTin(uint value)
		{
			return value > 0
				? Math.Max(1u, (value + 5) / 10)
				: 0;
		}

		// The world-conversation IIR carrier, with the framing every world IIR uses.
		private static Task SendWorldPacket(INetworkClient client, byte[] body)
		{
			return client.SendAsync(new Rpc(GameEndpoints.WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				s => s.WriteArrayUInt32Length(body)));
		}

		// The last sale per connection, so UndoLastSoldItem can restore it. Retail's undo is single-step too.
		private sealed record LastShopSale(int Container, int Slot, uint ItemId, uint Count, uint PriceTin);

		// The shop window the client currently has open, recorded when DispatchShopData is sent. BuyItem carries only
		// the session-volatile shop instance and a slot position, so the open window's wares are what resolve that
		// position back to a template id and price it. Keyed by Pos, the shop-window slot.
		private sealed record OpenShop(uint Instance, string VendorName, IReadOnlyDictionary<uint, ShopItem> WaresByPos);
	}
}
