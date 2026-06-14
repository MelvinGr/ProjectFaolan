using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Data;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network;
using Faolan.Core.Network.Conan;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Protobuf;
using Faolan.GameServer.Packets.FullCharSnapshot;
using Faolan.GameServer.Tradepost;
using Microsoft.Extensions.Logging;

// ReSharper disable once CheckNamespace
namespace Faolan.GameServer
{
	// Auction-house BUY -> mail delivery + the mailbox retrieval ops (TODO §9a Step 3), decoded from the necrol80
	// buy_from_autionhouse_get_mail capture (docs/tradepost-protocol.md "Buy -> mail delivery"). These ride the same
	// TradepostAgent the search/open path does, but they MUTATE state (money, listings, mail, the bag), so they live
	// on the GameServerListener partial (like ShopHandler/BankHandler) where the wallet/grant/persistence helpers are.
	//
	// Flow (capture-grounded): TradepostBuyItem [saleId] -> debit the buyer (DispatchUpdateCash) + create a SYSTEM
	// mail to the buyer with the bought item escrowed as an attachment + remove the listing; the client then polls the
	// mail (GetAllMails/GetMailByID -> DispatchMailMessage base), previews the item (GetItemData -> mail WITH
	// attachment), and TAKES it (GetMailItem -> DispatchAddItem grant + the now-itemless base mail). The bought item
	// renders via the proven GrantItemToBackpack path (the captured GetMailItem add uses the full inventory entry, not
	// the bare mail-attachment entry), so the item identity is the Items-template form (the listed INSTANCE's exact
	// Data quad isn't preserved into the bag yet - an interim, like vendor BUY).
	public partial class GameServerListener
	{
		// The recipient/sender endpoint class + per-attachment trailer constants the mail body carries (from the
		// capture; see Mail/MailAttachment). 0x08006F31 is the TradepostInterface object id, reused as the mail
		// endpoint class and the attachment trailer class.
		private const uint MailEndpointClass = 0x08006F31;
		private const uint MailFooterUnread = 0x0B; // footer word; flips to 0x09 once read (the client's read flag)
		private const uint MailAttachmentTrailerWord = 0x72;

		// TradepostBuyItem (0x200E) [saleId:u64][searchResultId:u64]: charge the buyer and deliver the listed item via
		// a system mail. Refuses on unknown listing or insufficient funds (no money moves until the mail is created).
		internal async Task HandleTradepostBuy(INetworkClient client, ConanPacket packet)
		{
			var character = client.Session.Character;
			if (character == null)
				return;

			var saleId = packet.Body.ReadUInt64(); // searchResultId follows but isn't needed to resolve the sale

			var listing = await Database.GetTradepostListingBySaleId(saleId);
			if (listing == null)
			{
				Logger.LogWarning("Tradepost BUY: {Char} bought unknown listing saleId=0x{Sale:X16} (already sold?).", character.Name, saleId);
				return;
			}

			if (character.Money < listing.Price)
			{
				Logger.LogInformation("Tradepost BUY: {Char} can't afford listing 0x{Sale:X16} ({Price} tin, has {Balance}).",
					character.Name, saleId, listing.Price, character.Money);
				return;
			}

			character.Money -= listing.Price;
			await Database.SetCharacterMoney(character.Id, character.Money);
			await SendMoneyUpdate(client, character.Money, MoneyUpdateReason.Spend);

			var mail = await BuildPurchaseMail(character.Name, listing);
			await Database.CreateMail(mail);
			await Database.DeleteTradepostListing(saleId);

			// Push the mail (base, no attachment): it carries its own MailId, which the client then targets with the
			// GetMail* ops to preview + take the item. Echo the inbound TradepostInterface instance.
			await TradepostResponses.SendMailMessage(client, mail, includeAttachments: false, packet.Sender.Instance);

			Logger.LogInformation("Tradepost BUY: {Char} bought listing 0x{Sale:X16} (item 0x{Item:X}) for {Price} tin -> mail 0x{Mail:X16} (wallet {Balance}).",
				character.Name, saleId, listing.ItemId, listing.Price, mail.MailId, character.Money);
		}

		// The mailbox ops (GetAllMails / GetMailByID / GetItemData preview / GetMailItem take / MarkMailRead /
		// DeleteMail). All address the buyer's own mail by the wire MailId; GetMailItem grants the escrowed item.
		internal async Task HandleTradepostMailOp(INetworkClient client, ConanPacket packet, TradepostProtocol.AgentMethod method)
		{
			var character = client.Session.Character;
			if (character == null)
				return;

			var inst = packet.Sender.Instance;

			switch (method)
			{
				case TradepostProtocol.AgentMethod.GetAllMails:
				{
					var mails = await Database.GetMailsForRecipient(character.Name);
					foreach (var m in mails)
						await TradepostResponses.SendMailMessage(client, m, includeAttachments: false, inst);
					await TradepostResponses.SendRetrieveMailResult(client, inst);
					Logger.LogInformation("Tradepost mail: {Char} polled the mailbox -> {Count} mail(s).", character.Name, mails.Count);
					break;
				}

				case TradepostProtocol.AgentMethod.GetMailByID:
				{
					var mail = await Database.GetMailByWireId(packet.Body.ReadUInt64());
					if (mail != null)
						await TradepostResponses.SendMailMessage(client, mail, includeAttachments: false, inst);
					break;
				}

				case TradepostProtocol.AgentMethod.GetItemData:
				{
					// Preview: the mail WITH its attachment (the client shows the item in the mail; nothing is granted).
					var mail = await Database.GetMailByWireId(packet.Body.ReadUInt64());
					if (mail != null)
						await TradepostResponses.SendMailMessage(client, mail, includeAttachments: true, inst);
					break;
				}

				case TradepostProtocol.AgentMethod.GetMailItem:
				{
					var mailId = packet.Body.ReadUInt64();
					var itemInstanceId = packet.Body.ReadUInt64();
					await TakeMailItem(client, mailId, itemInstanceId, inst);
					break;
				}

				case TradepostProtocol.AgentMethod.MarkMailRead:
				{
					var mailId = packet.Body.ReadUInt64();
					await Database.SetMailRead(mailId, true);
					await TradepostResponses.SendMailResult(client, success: true, error: 0, inst);
					break;
				}

				case TradepostProtocol.AgentMethod.DeleteMail:
				{
					var mailId = packet.Body.ReadUInt64();
					await Database.DeleteMail(mailId);
					Logger.LogInformation("Tradepost mail: {Char} deleted mail 0x{Mail:X16}.", character.Name, mailId);
					break;
				}
			}
		}

		// GetMailItem: grant the escrowed attachment into the bag, then drop it from the mail and re-send the
		// now-itemless mail so the client clears the attachment. The item is RENDERED live from the attachment's
		// stored INSTANCE identity (Data quad + template + level + hash) - the same fields the AH grid renders from -
		// so it works even when the listed template isn't in our Items table (most AH listings reference retail
		// templates we never harvested, and listing.ItemHash/archetype is 0). Persistence is conditional: only a real
		// buildable Items row can be written to CharacterInventory (the ItemId FK requires it) and re-rendered by the
		// world-entry snapshot, so an un-harvested item is a LIVE-ONLY delivery (kept this session, lost on relog).
		// Aborts (item left in the mail) only on a full bag, so the player can retry.
		private async Task TakeMailItem(INetworkClient client, ulong mailId, ulong itemInstanceId, uint inst)
		{
			var character = client.Session.Character;
			var mail = await Database.GetMailByWireId(mailId);
			var attachment = mail?.Attachments?.FirstOrDefault(a => a.ItemInstanceId == itemInstanceId)
				?? mail?.Attachments?.FirstOrDefault();
			if (mail == null || attachment == null)
			{
				Logger.LogWarning("Tradepost mail: {Char} tried to take item 0x{Item:X16} from mail 0x{Mail:X16} (no such attachment).",
					character.Name, itemInstanceId, mailId);
				return;
			}

			var slot = await FindFreeBagSlot(character.Id);
			if (slot == null)
			{
				Logger.LogWarning("Tradepost mail: {Char}'s bag is full; mail 0x{Mail:X16} item kept in mail.", character.Name, mailId);
				return;
			}

			var entry = BuildMailItemEntry(attachment, character.Id, slot.Value);
			await SendAddItem(client, InventoryContainerBackpack, (uint)slot.Value, entry);

			var qty = attachment.Quantity == 0 ? 1u : attachment.Quantity;
			var item = await Database.GetItem(attachment.Template);
			if (item != null)
				// The listing importer harvests every listed item into Items (so the CharacterInventory.ItemId FK
				// holds), letting the bought item persist. Archetype-0 items the world-entry snapshot drops are
				// re-rendered on relog by RedeliverNonSnapshotBagItems via the live add path.
				await Database.SetCharacterInventorySlot(character.Id, (int)FullCharSnapshotBuilder.BagKind, slot.Value, attachment.Template, qty);
			else
				Logger.LogInformation("Tradepost mail: delivered template {Template} to {Char} LIVE-ONLY (no Items row; won't persist across relog).",
					attachment.Template, character.Name);

			await Database.RemoveMailAttachment(mailId, attachment.ItemInstanceId);
			mail.Attachments.Remove(attachment);
			await TradepostResponses.SendMailMessage(client, mail, includeAttachments: false, inst);

			Logger.LogInformation("Tradepost mail: {Char} took item (template {Template}) from mail 0x{Mail:X16} into bag slot {Slot}.",
				character.Name, attachment.Template, mailId, slot);
		}

		// Builds the live DispatchAddItem entry for a mail attachment from its stored instance identity (no Items
		// lookup), mirroring SnapshotItemBuilder's bag entry: ProtoAcgItem{key=Data quad, template1/2=template,
		// level, hash} + the bag stat triple (0=flags, 23=recordId, 26=count). The client resolves the visual from
		// the template via its RDB, so this renders even for items we don't have as Items rows.
		private static ProtoInventoryGameEntry BuildMailItemEntry(MailAttachment a, uint charId, int slot)
		{
			var key = new ProtoDecryptionKey { Key1 = a.Data1, Key2 = a.Data2, Key3 = a.Data3, Key4 = a.Data4 };
			var qty = a.Quantity == 0 ? 1u : a.Quantity;
			var entry = new ProtoInventoryGameEntry
			{
				ItemId = FullCharSnapshotBuilder.ItemId(charId, FullCharSnapshotBuilder.BagKind, (ulong)slot),
				ItemData = new ProtoAcgItem
				{
					Key1 = key,
					Key2 = key,
					Key3 = key,
					Template1 = a.Template,
					Template2 = a.Template,
					Level = a.ItemLevel,
					Hash = a.ItemHash
				}
			};
			entry.StatLists.Add(new ProtoInventoryGameEntry.StatValue { Stat = 0, Value = 1 });
			entry.StatLists.Add(new ProtoInventoryGameEntry.StatValue { Stat = 23, Value = (uint)FullCharSnapshotBuilder.RecordId(charId, FullCharSnapshotBuilder.BagKind, (ulong)slot) });
			entry.StatLists.Add(new ProtoInventoryGameEntry.StatValue { Stat = 26, Value = qty });
			return entry;
		}

		// Builds the system mail for a completed AH purchase: recipient = the buyer, sender empty (system), the bought
		// item escrowed as an attachment, body = the TradePostGUI_MailItemBought remoteformat. The item-name fragment
		// is left empty (the per-item RDB name key is unsynthesizable - see TradepostMailBody); the item still
		// delivers, only its name in the mail text degrades.
		private async Task<Mail> BuildPurchaseMail(string buyer, TradepostListing listing)
		{
			var mail = new Mail
			{
				MailId = await AllocateMailId(),
				RecipientClass = MailEndpointClass,
				RecipientName = buyer,
				SenderClass = MailEndpointClass,
				SenderName = "",
				BodyXml = TradepostMailBody.BuildItemBought(
					buyer, listing.SaleId, itemNameEscaped: "", listing.SellerName, listing.Price.ToString()),
				FooterWord = MailFooterUnread,
				Money = 0,
				Timestamp1 = 0,
				Timestamp2 = 0
			};

			mail.Attachments.Add(new MailAttachment
			{
				ItemInstanceId = listing.SaleId,
				Quantity = listing.Quantity == 0 ? 1 : listing.Quantity,
				Data1 = listing.Data1,
				Data2 = listing.Data2,
				Data3 = listing.Data3,
				Data4 = listing.Data4,
				Template = listing.Template,
				ItemLevel = listing.ItemLevel,
				ItemHash = listing.ItemHash,
				TrailerWord = MailAttachmentTrailerWord,
				TrailerClass = MailEndpointClass,
				TrailerTail = 0
			});

			return mail;
		}

		// A unique wire MailId. The id is session/instance-volatile (like entity handles), so a random 64-bit value
		// is fine; retry on the rare collision with an existing persisted mail.
		private async Task<ulong> AllocateMailId()
		{
			for (var attempt = 0; attempt < 8; attempt++)
			{
				var id = (ulong)System.Random.Shared.NextInt64(1, long.MaxValue);
				if (await Database.GetMailByWireId(id) == null)
					return id;
			}

			return (ulong)System.Random.Shared.NextInt64(1, long.MaxValue);
		}
	}
}
