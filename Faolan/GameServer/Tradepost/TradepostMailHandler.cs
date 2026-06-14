using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Data;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network;
using Faolan.Core.Network.Conan;
using Faolan.Core.Network.Packets;
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

		// GetMailItem: grant the escrowed attachment into the bag (via the proven GrantItemToBackpack render path),
		// then drop it from the mail and re-send the now-itemless mail so the client clears the attachment. Aborts
		// (item left in the mail) on a full bag / un-buildable item, so the player can retry.
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

			var slot = await GrantItemToBackpack(client, attachment.Template, attachment.Quantity);
			if (slot == null)
			{
				Logger.LogWarning("Tradepost mail: couldn't grant item template {Template} from mail 0x{Mail:X16} to {Char} (bag full or not buildable); kept in mail.",
					attachment.Template, mailId, character.Name);
				return;
			}

			await Database.RemoveMailAttachment(mailId, attachment.ItemInstanceId);
			mail.Attachments.Remove(attachment);
			await TradepostResponses.SendMailMessage(client, mail, includeAttachments: false, inst);

			Logger.LogInformation("Tradepost mail: {Char} took item template {Template} from mail 0x{Mail:X16} into bag slot {Slot}.",
				character.Name, attachment.Template, mailId, slot);
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
