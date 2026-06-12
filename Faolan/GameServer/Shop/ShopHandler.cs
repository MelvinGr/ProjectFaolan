using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Data;
using Faolan.Core.Network;
using Faolan.Core.Network.Conan;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Packets.Opcodes;
using Microsoft.Extensions.Logging;

// ReSharper disable once CheckNamespace
namespace Faolan.GameServer
{
	// NPC vendor / shop flow (TODO §9b), decoded from the necrol80 sell_items_to_trader capture:
	//   DL 0x2033 DispatchShopData   [shop ID32 (type 0xC75B)][vendor NPC ID32][protobuf shop content]
	//   UL 0x2034 SellItem           [shop ID32][u32 container][u32 charId][u32 slot]
	//   DL 0x2035 DispatchItemSoldToShop [u32 container][u32 charId][u32 slot]  (the client clears the bag slot)
	//   DL 0x2005 money update (the wallet credit - see Send0x2005_MoneyUpdate)
	//   UL 0x2033 BuyItem            [shop ID32][u32 pos]
	//   UL 0x2032 CloseShop, UL 0x2035 UndoLastSoldItem
	// The wire container 0x68 (104) IS the DB CharacterInventory.Container bag value - no mapping needed.
	public partial class GameServerListener
	{
		// The last sale per connection, so UndoLastSoldItem can restore it (retail's undo is single-step too).
		private sealed record LastShopSale(int Container, int Slot, uint ItemId, uint Count, uint PriceTin);

		private readonly ConcurrentDictionary<INetworkClient, LastShopSale> _lastShopSale = new();

		// Provisional vendor price in tin, the wallet wire unit (the RDB item templates carry the real value -
		// TEA-cracked but the value field is not mapped yet; the captured sales went for 19/23/92/392 tin);
		// until then, scale with item level so better junk pays more.
		internal static uint VendorPriceTin(byte itemLevel, uint count)
		{
			return (uint)System.Math.Max(1, itemLevel * 2) * System.Math.Max(1u, count);
		}

		// DL 0x2035 - tells the client the bag slot was sold (it clears the slot itself).
		// Body golden-tested against the captured slot-5 sale (ShopTests).
		internal static void WriteItemSoldBody(FaolanStream s, uint container, uint characterId, uint slot)
		{
			s
				.WriteUInt32(container)
				.WriteUInt32(characterId)
				.WriteUInt32(slot);
		}

		// UL 0x2034 SellItem: validate the bag slot, price the item, remove it, credit the wallet, and confirm
		// with DispatchItemSoldToShop + the 0x2005 balance update (retail's exact reply trio minus the 0x201C
		// record-stream entry, which is bookkeeping we don't model).
		private async Task HandleSellItemToShop(INetworkClient client, ConanPacket packet)
		{
			packet.Body.ReadUInt32(); // shop ID32 type (0xC75B)
			var shopId = packet.Body.ReadUInt32();
			var container = packet.Body.ReadUInt32();
			var sellerId = packet.Body.ReadUInt32();
			var slot = packet.Body.ReadUInt32();

			var character = client.Session.Character;
			if (character == null)
				return;

			var row = (await Database.GetCharacterInventory(character.Id))
				.FirstOrDefault(i => i.Container == (int)container && i.Slot == (int)slot);
			if (row?.Item == null)
			{
				Logger.LogWarning("SellItem to shop {Shop:X}: no item in container {Container} slot {Slot} for {Character}.", shopId, container, slot, character.Name);
				return;
			}

			var price = VendorPriceTin(row.Item.Level, row.Count);
			await Database.SetCharacterInventorySlot(character.Id, (int)container, (int)slot, null);
			character.Money += price;
			await Database.SaveCharacter(character);
			_lastShopSale[client] = new LastShopSale((int)container, (int)slot, row.ItemId ?? 0, row.Count, price);

			var ps = new PacketStream();
			ps.WriteHeader(packet.ReceiverBytes, packet.SenderBytes, GameServerRespondOpcodes.ItemSoldToShop);
			WriteItemSoldBody(ps, container, sellerId, slot);
			await ps.SendAsync(client);
			await SendMoneyUpdate(client, character.Money, MoneyUpdateReason.Gain);

			Logger.LogInformation("Shop: {Character} sold \"{Item}\" x{Count} (container {Container} slot {Slot}) for {Price} tin (wallet {Balance}).",
				character.Name, row.Item.Name, row.Count, container, slot, price, character.Money);
		}

		// UL 0x2035 UndoLastSoldItem: restore the last sale (item back to its slot, price debited).
		private async Task HandleUndoLastSoldItem(INetworkClient client)
		{
			var character = client.Session.Character;
			if (character == null || !_lastShopSale.TryRemove(client, out var sale) || sale.ItemId == 0)
			{
				Logger.LogInformation("UndoLastSoldItem: nothing to undo.");
				return;
			}

			await Database.SetCharacterInventorySlot(character.Id, sale.Container, sale.Slot, sale.ItemId, sale.Count);
			character.Money = character.Money >= sale.PriceTin
				? character.Money - sale.PriceTin
				: 0;
			await Database.SaveCharacter(character);
			await SendMoneyUpdate(client, character.Money, MoneyUpdateReason.Spend);

			Logger.LogInformation("Shop: {Character} undid the sale of item {ItemId} (slot {Slot}); {Price} tin debited (wallet {Balance}).",
				character.Name, sale.ItemId, sale.Slot, sale.PriceTin, character.Money);
		}

		// .shop [vendor name]  - open a vendor's shop window. The content protobuf is DECODED (a plain
		// ProtoInventoryGame with container type 111, ShopContentCodec) so the window is CONSTRUCTED from the
		// imported Shop/ShopItem + Items rows; with no imported shops the captured trader blob is the fallback.
		// The real trigger is the vendor NPC dialogue (KnubotStartTrade), still conversation-gated.
		private async Task OpenShopCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var vendor = (arguments ?? "").Trim();
			var shop = vendor.Length > 0
				? await Database.GetShopByVendorName(vendor)
				: await Database.GetFirstShop();

			byte[] body;
			string label;
			if (shop != null)
			{
				// Resolve each ware's identity from Items (harvested from the same captures by the importer).
				var templates = shop
					.Items
					.Select(i => i.ItemTemplateId)
					.Distinct()
					.ToList();
				var resolved = new Dictionary<uint, Core.Database.Tables.Item>();
				foreach (var template in templates)
					resolved[template] = await Database.GetItem(template);

				body = ShopContentCodec.Build(shop.Id, 0, shop.MaxSize, shop.Items, t => resolved.GetValueOrDefault(t));
				label = $"\"{shop.VendorName}\" ({shop.Items.Count} wares, DB-built)";
			}
			else
			{
				body = Shop.ShopReplayData.DispatchShopDataBody;
				label = "the captured trader (PoC blob; run the importer to get DB-built shops)";
			}

			await new PacketStream()
				.WriteHeader(LoginWorld201CSender, LoginWorld201CReceiver(client), GameServerRespondOpcodes.ShopData)
				.WriteArray(body)
				.SendAsync(client);
			await _agentServerDispatcher.SendSystemMessage(characterId, $"Opened {label}. Try selling junk from your bag.");
		}
	}
}
