using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Economy
{
	// The auction house (Tradepost) + the mailbox, decoded from the NecroL80 sell/buy/mail captures
	// (docs/subsystems/tradepost-protocol.md). Extracted out of GameServerListener as a composed collaborator
	// (#200): it owns the listing/mail transactions and the auctioneer-template cache, and reaches the two
	// cross-cutting OUTBOUND ops it needs - the wallet update and the live item grant - through the
	// IEconomyOutbound port the listener implements, exactly as ShopService/BankService do.
	//
	// Everything that is purely a SEND stayed in TradepostResponses (the addressing + framing); this class holds
	// the transactions. `Handle` returns false for a method it does not own so the LISTENER keeps ownership of the
	// unknown-packet tracer, which is connection-level bookkeeping rather than economy logic.
	public sealed class TradepostService
	{
		// Bank-slot overlay flag values the client draws a listed / mail-pending cell from (captures F376/F398/F500).
		private const uint BankFlagListed = 0x401; // flags value while listed for sale
		private const uint BankFlagMailPending = 0x8001; // flags value while attached to an outgoing mail
		private const uint BankFlagNormal = 0x001; // flags value once unlisted / mail cancelled

		private readonly IDatabaseRepository _database;
		private readonly ILogger _logger;
		private readonly IEconomyOutbound _outbound;

		// The NPC TEMPLATES that operate an auction house (#647). Talking to one opens the Tradepost -
		// GameServerListener.Inbound's TalkToNpc auctioneer branch.
		//
		// The role comes from the CLIENT's own spawn data (`NpcStats.profession_type` -> `NpcStatCore.ProfessionType`),
		// not from the display name "Trader" this used to match on. A name was the wrong key twice over: "Trader" is a
		// ROLE that four distinct templates play, so the name pinned none of them, and any other NPC that happened to
		// carry the string would have opened the auction house. The profession tag is exclusive - those same four
		// templates are the only ones in the world carrying it - so this now recognises every auctioneer rather than
		// whichever one a capture saw.
		private HashSet<uint>? _auctioneerTemplates;

		public TradepostService(IDatabaseRepository database, ILogger logger, IEconomyOutbound outbound)
		{
			_database = database;
			_logger = logger;
			_outbound = outbound;
		}

		public async Task<bool> IsAuctioneerTemplate(uint npcTemplateId)
		{
			_auctioneerTemplates ??= [.. await _database.GetNpcTemplatesByProfession(NpcProfession.Auctioneer)];
			return _auctioneerTemplates.Contains(npcTemplateId);
		}

		// Inbound Tradepost (auction house) + mail dispatch, routed here by receiver class (TradepostAgentType) from
		// the listener's ReceivedPacket. ItemSearch parses + replies via TradepostResponses; the mutating ops are the
		// methods below. Grammar in docs/subsystems/tradepost-protocol.md.
		//
		// Returns false for a method this service does not own, so the caller can log it as unhandled.
		public async Task<bool> Handle(INetworkClient client, Rpc packet, FaolanStream body)
		{
			var method = (TradepostAgentMethods)packet.Method;
			switch (method)
			{
				case TradepostAgentMethods.UpdateItemTimeouts:
				{
					// Sent (empty body) AFTER the client received DispatchOpenTradepost (auctioneer / .ah) - the
					// observed "window opened" signal; nothing to send back, just log.
					_logger.LogInformation("Tradepost: {Char} opened the auction house.", client.Session.Character.Name);
					break;
				}

				case TradepostAgentMethods.ItemSearch:
				{
					var query = TradepostItemSearchCodec.Parse(body.ReadRemainingArray());
					var listings = await _database.GetTradepostListings(query.ItemId, client.Session.Character.RealmId); // #324: realm-local market
					_logger.LogInformation("Tradepost: {Char} searched lang={Lang} itemId=0x{Item:X} page={Page} -> {Count} listing(s).", client.Session.Character!.Name, query.Lang, query.ItemId,
						query.Page, listings.Length);

					// Always send the real result: an empty match list yields an empty grid (head = (0+1)*0x3F1, no rows).
					await TradepostResponses.SendSearchResults(client, packet.Sender.Instance, listings);
					break;
				}

				case TradepostAgentMethods.BuyItem:
				{
					// BUY -> debit the buyer + deliver the item via a system mail.
					await HandleBuy(client, packet, body);
					break;
				}

				case TradepostAgentMethods.TradepostSellItem:
				{
					// SELL -> list a bank item for sale (creates a listing + escrows the item out of the bank).
					await HandleSell(client, body);
					break;
				}

				case TradepostAgentMethods.TradepostChangeItemPrice:
				{
					await HandleChangePrice(client, body);
					break;
				}

				case TradepostAgentMethods.TradepostCancelSellItem:
				{
					await HandleCancelSell(client, body);
					break;
				}

				case TradepostAgentMethods.SendMail:
				{
					await HandleSendMail(client, packet, body);
					break;
				}

				case TradepostAgentMethods.CancelMail:
				{
					await HandleCancelMail(client, body);
					break;
				}

				case TradepostAgentMethods.Close:
				{
					_logger.LogInformation("Tradepost: {Char} closed the auction house.", client.Session.Character.Name);
					break;
				}

				// Mailbox retrieval ops: poll / preview / take the bought item / mark-read / delete.
				case TradepostAgentMethods.GetAllMails:
				case TradepostAgentMethods.GetMailByID:
				case TradepostAgentMethods.GetItemData:
				case TradepostAgentMethods.GetMailItem:
				case TradepostAgentMethods.MarkMailRead:
				case TradepostAgentMethods.DeleteMail:
				{
					await HandleMailOp(client, packet, body, method);
					break;
				}

				default:
				{
					return false;
				}
			}

			return true;
		}

		// The next wire MailId: a deterministic sequence (max + 1), not a random id + collision retry.
		private async Task<ulong> AllocateMailId()
		{
			return await _database.GetNextMailId();
		}

		// Builds the system mail for a completed AH purchase: recipient = the buyer, sender empty (system), the bought
		// item escrowed as an attachment, body = the TradePostGUI_MailItemBought remoteformat. The item-name fragment
		// is left empty (the per-item RDB name key is unsynthesizable - see MailColumns.ItemBoughtBody); the item still
		// delivers, only its name in the mail text degrades.
		private async Task<Mail> BuildPurchaseMail(string buyer, TradepostListing listing)
		{
			var now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

			var mail = new Mail
			{
				MailId = await AllocateMailId(),
				RecipientClass = MailConstants.EndpointClass,
				RecipientName = buyer,
				SenderClass = MailConstants.EndpointClass,
				SenderName = "",
				BodyXml = MailColumns.ItemBoughtBody(buyer, listing.SaleId, listing.SellerName ?? "", listing.Price.ToString()),
				FooterWord = MailConstants.FooterUnread,
				Money = 0,
				Timestamp1 = now,
				Timestamp2 = now + MailConstants.ExpirySeconds
			};

			mail.Attachments.Add(new MailAttachment
			{
				ItemInstanceId = listing.SaleId,
				Quantity = listing.Quantity == 0
					? 1
					: listing.Quantity,
				Template = listing.Template, // the key lives in Items (FK on Template); no inline copy
				ItemLevel = listing.ItemLevel,
				ItemHash = listing.ItemHash,
				TrailerWord = MailConstants.AttachmentTrailerWord,
				TrailerClass = MailConstants.EndpointClass,
				TrailerTail = 0
			});

			return mail;
		}

		// Banks any money attached to a mail into the recipient's wallet and clears it from the mail (#213). Idempotent -
		// a 0-money mail is a no-op - so it's safe to call on every take. Without this the tin was never credited and the
		// unclaimed money kept the client's delete button greyed out (a mail with attachments can't be deleted).
		private async Task GrantMailMoney(INetworkClient client, Mail mail)
		{
			if (mail == null || mail.Money == 0)
				return;

			var character = client.Session.Character;
			var credited = mail.Money;
			character.Money += credited;
			await _database.SetCharacterMoney(character.Id, character.Money);
			await _outbound.SendUpdateCash(client, character.Money, UpdateCashReason.Gain);
			await _database.SetMailMoney(mail.MailId, 0);
			mail.Money = 0;
			_logger.LogInformation("Tradepost mail: {Char} banked {Tin} tin from mail 0x{Mail:X16}.", character.Name, credited, mail.MailId);
		}

		// TradepostBuyItem (0x200E) [saleId:u64][searchResultId:u64]: charge the buyer and deliver the listed item via
		// a system mail. Refuses on unknown listing or insufficient funds (no money moves until the mail is created).
		private async Task HandleBuy(INetworkClient client, Rpc packet, FaolanStream body)
		{
			var character = client.Session.Character;

			var saleId = body.ReadUInt64();
			body.ReadUInt64(); // searchResultId - the row in the client's own search result; the saleId resolves the sale

			var listing = await _database.GetTradepostListingBySaleId(saleId, character.RealmId);
			if (listing == null)
			{
				_logger.LogWarning("Tradepost BUY: {Char} bought unknown listing saleId=0x{Sale:X16} (already sold?).", character.Name, saleId);
				return;
			}

			if (character.Money < listing.Price)
			{
				_logger.LogInformation("Tradepost BUY: {Char} can't afford listing 0x{Sale:X16} ({Price} tin, has {Balance}).", character.Name, saleId, listing.Price, character.Money);
				return;
			}

			character.Money -= listing.Price;
			await _database.SetCharacterMoney(character.Id, character.Money);
			await _outbound.SendUpdateCash(client, character.Money, UpdateCashReason.Spend);

			var mail = await BuildPurchaseMail(character.Name!, listing);
			mail.RealmId = character.RealmId; // #324: no cross-realm mail (recipient = buyer)
			await _database.CreateMail(mail);
			await _database.DeleteTradepostListing(saleId, character.RealmId);

			// The listed item stayed in the SELLER's bank (with a "listed" overlay) until now - the sale removes it
			// from their bank. Importer-seeded listings have no real seller character, so this is a no-op for them.
			var seller = await _database.GetCharacterByName(listing.SellerName, character.RealmId);
			if (seller != null && seller.Id != character.Id)
				await _database.SetCharacterItemSlot(seller.Id, ContainerKind.Bank, (int)listing.BankSlot, null);

			// Push the mail (base, no attachment): it carries its own MailId, which the client then targets with the
			// GetMail* ops to preview + take the item. Echo the inbound TradepostInterface instance.
			await TradepostResponses.SendMailMessage(client, mail, false, packet.Sender.Instance);

			_logger.LogInformation("Tradepost BUY: {Char} bought listing 0x{Sale:X16} (item 0x{Item:X}) for {Price} tin -> mail 0x{Mail:X16} (wallet {Balance}).", character.Name, saleId,
				listing.ItemId, listing.Price, mail.MailId, character.Money);
		}

		// CancelMail (0x2009) [mailId:u64]: recall/delete one of the player's mails (capture F403). We delete it; any
		// escrowed attachment is dropped with it (the AH buy-delivery mail is the only attachment source today).
		private async Task HandleCancelMail(INetworkClient client, FaolanStream body)
		{
			var character = client.Session.Character;

			var mailId = body.ReadUInt64();

			// If the sender (this player) attached a bank item, clear its mail-pending overlay (capture F404: flag -> 1)
			// - the item never left the bank, so cancelling just frees it.
			var mail = await _database.GetMailByWireId(mailId);
			if (mail != null && mail.SenderName == character.Name && (mail.Attachments?.Count ?? 0) > 0)
				await TradepostResponses.SendBankSlotStat(client, mail.SenderBankSlot, TradepostListingCodec.StatFlags, BankFlagNormal);

			await _database.DeleteMail(mailId);
			_logger.LogInformation("Tradepost mail: {Char} cancelled/deleted mail 0x{Mail:X16}.", character.Name, mailId);
		}

		// TradepostCancelSellItem (0x200C) [nBankItemSlot:u32]: pull the seller's own listing (capture F397 = slot 6).
		// The item never left the bank (it just had the "listed" overlay), so cancelling only deletes the listing -
		// the client clears the overlay itself.
		private async Task HandleCancelSell(INetworkClient client, FaolanStream body)
		{
			var character = client.Session.Character;

			var bankSlot = body.ReadUInt32();

			var listing = await _database.GetTradepostListingBySellerSlot(character.Name!, bankSlot, character.RealmId);
			if (listing == null)
			{
				_logger.LogWarning("Tradepost CANCELSELL: {Char} has no listing for bank slot {Slot}.", character.Name, bankSlot);
				return;
			}

			await _database.DeleteTradepostListing(listing.SaleId, character.RealmId);
			// Clear the bank-slot overlay (capture F398): flag back to normal, price to 0.
			await TradepostResponses.SendBankSlotStat(client, bankSlot, TradepostListingCodec.StatFlags, BankFlagNormal);
			await TradepostResponses.SendBankSlotStat(client, bankSlot, TradepostListingCodec.StatPrice, 0);
			_logger.LogInformation("Tradepost CANCELSELL: {Char} cancelled listing 0x{Sale:X16} (bank slot {Slot}); item stays in the bank.", character.Name, listing.SaleId, bankSlot);
		}

		// TradepostChangeItemPrice (0x200B) [nBankItemSlot:u32][nNewPrice:u64]: reprice the seller's own listing,
		// addressed by the origin bank slot (capture F363 = slot 6 -> 5,000,000 tin). No tradepost reply (re-poll).
		private async Task HandleChangePrice(INetworkClient client, FaolanStream body)
		{
			var character = client.Session.Character;

			var bankSlot = body.ReadUInt32();
			var newPrice = body.ReadUInt64();

			var listing = await _database.GetTradepostListingBySellerSlot(character.Name!, bankSlot, character.RealmId);
			if (listing == null)
			{
				_logger.LogWarning("Tradepost CHANGEPRICE: {Char} has no listing for bank slot {Slot}.", character.Name, bankSlot);
				return;
			}

			await _database.SetTradepostListingPrice(listing.SaleId, character.RealmId, newPrice);
			// Update the bank-slot overlay's price stat (capture F364).
			await TradepostResponses.SendBankSlotStat(client, bankSlot, TradepostListingCodec.StatPrice, (uint)newPrice);
			_logger.LogInformation("Tradepost CHANGEPRICE: {Char} repriced listing 0x{Sale:X16} (bank slot {Slot}) {Old} -> {New} tin.", character.Name, listing.SaleId, bankSlot, listing.Price,
				newPrice);
		}

		// The mailbox ops (GetAllMails / GetMailByID / GetItemData preview / GetMailItem take / MarkMailRead /
		// DeleteMail). All address the buyer's own mail by the wire MailId; GetMailItem grants the escrowed item.
		private async Task HandleMailOp(INetworkClient client, Rpc packet, FaolanStream body, TradepostAgentMethods method)
		{
			var character = client.Session.Character;

			var inst = packet.Sender.Instance;

			switch (method)
			{
				case TradepostAgentMethods.GetAllMails:
				{
					var mails = await _database.GetMailsForRecipient(character.Name!, character.RealmId);
					foreach (var m in mails)
						await TradepostResponses.SendMailMessage(client, m, false, inst);
					await TradepostResponses.SendRetrieveMailResult(client, inst);
					_logger.LogInformation("Tradepost mail: {Char} polled the mailbox -> {Count} mail(s).", character.Name, mails.Length);
					break;
				}

				case TradepostAgentMethods.GetMailByID:
				{
					var mail = await _database.GetMailByWireId(body.ReadUInt64());
					if (mail != null)
						await TradepostResponses.SendMailMessage(client, mail, false, inst);
					break;
				}

				case TradepostAgentMethods.GetItemData:
				{
					// Preview: the mail WITH its attachment (the client shows the item in the mail; nothing is granted).
					var mail = await _database.GetMailByWireId(body.ReadUInt64());
					if (mail != null)
						await TradepostResponses.SendMailMessage(client, mail, true, inst);
					break;
				}

				case TradepostAgentMethods.GetMailItem:
				{
					var mailId = body.ReadUInt64();
					var itemInstanceId = body.ReadUInt64();
					await TakeMailItem(client, mailId, itemInstanceId, inst);
					break;
				}

				case TradepostAgentMethods.MarkMailRead:
				{
					var mailId = body.ReadUInt64();
					await _database.SetMailRead(mailId, true);
					await TradepostResponses.SendMailResult(client, true, 0, inst);
					break;
				}

				case TradepostAgentMethods.DeleteMail:
				{
					var mailId = body.ReadUInt64();
					await _database.DeleteMail(mailId);
					_logger.LogInformation("Tradepost mail: {Char} deleted mail 0x{Mail:X16}.", character.Name, mailId);
					break;
				}
			}
		}

		// TradepostSellItem (0x200A) [nBankItemSlot:u32][nPrice:u64]: list one of the player's BANK items (container
		// 105) for sale. Decoded from the NecroL80 sell_item_on_ah capture (F375 = slot 6 @ 1,000,000 tin). Creates a
		// TradepostListing from the bank item's identity, escrows the item out of the bank, and refreshes the bank tab.
		private async Task HandleSell(INetworkClient client, FaolanStream body)
		{
			var character = client.Session.Character;

			var bankSlot = (int)body.ReadUInt32();
			var price = body.ReadUInt64();

			var listed = await _database.GetCharacterItemSlot(character.Id, ContainerKind.Bank, bankSlot);
			if (listed == null)
			{
				_logger.LogWarning("Tradepost SELL: {Char} has no bank item at slot {Slot}.", character.Name, bankSlot);
				return;
			}

			// #181: file the listing under the item's AH search CATEGORY (the item-type-search id the client queries
			// by, e.g. 30030) so a normal item-type search surfaces it, recovered from captured listings. Falls back
			// to the template when the category is unknown (no regression vs the old template-filing).
			var searchCategory = await _database.GetItemSearchCategory(listed.TemplateId) ?? listed.TemplateId;

			var listing = new TradepostListing
			{
				SaleId = await _database.GetNextTradepostSaleId(character.RealmId),
				Handle = 0,
				RealmId = character.RealmId, // #324: the AH is realm-local (the seller's realm)
				SellerName = character.Name!,
				BankSlot = (uint)bankSlot,
				Price = price,
				Quantity = listed.Count == 0
					? 1u
					: listed.Count,
				ItemId = searchCategory, // #181: the item-type search category (was the template id)
				InstanceField = 0,
				ItemHandle = 0,
				EntryFlags = TradepostListingCodec.DefaultEntryFlags,
				Timestamp = 0,
				Template = listed.TemplateId, // the key lives in Items (FK on Template); no inline copy
				ItemLevel = listed.Template.Level,
				ItemHash = listed.Template.Hash
			};
			await _database.CreateTradepostListing(listing);

			// The listed item STAYS in its bank slot - the client draws a "listed" overlay when the server pushes
			// these three bank-slot stat updates (decoded from the capture, F376/F377): price, timestamp, and the
			// listed flag. It only leaves the seller's bank when someone BUYS it (HandleBuy clears the cell).
			await TradepostResponses.SendBankSlotStat(client, (uint)bankSlot, TradepostListingCodec.StatPrice, (uint)price);
			await TradepostResponses.SendBankSlotStat(client, (uint)bankSlot, TradepostListingCodec.StatTimestamp, (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
			await TradepostResponses.SendBankSlotStat(client, (uint)bankSlot, TradepostListingCodec.StatFlags, BankFlagListed);

			// Retail pushes a 0x2005 wallet refresh after a listing - but the balance is UNCHANGED: there is NO
			// listing deposit / postage fee. Verified across the sell / send-mail / change-price / cancel captures
			// (NecroL80 stays at 1,365,148 tin throughout), #182. Push the current balance (reason 0, as captured) so
			// the HUD stays in sync; no debit.
			await _outbound.SendUpdateCash(client, character.Money, UpdateCashReason.Spend);

			_logger.LogInformation("Tradepost SELL: {Char} listed \"{Item}\" x{Qty} (bank slot {Slot}, template {Template}) for {Price} tin -> sale 0x{Sale:X16}.", character.Name, listed.Template.Name,
				listing.Quantity, bankSlot, listed.TemplateId, price, listing.SaleId);
		}

		// SendMail (0x2002) [u16+recipient][u16+body][money:u64][attachments: ListFrame word + u32 slot each]: a
		// player-composed mail, optionally with a bank item attached (capture F499 = to "DemoL80", "Hello this is a
		// test", slot 5, no COD - its word is 0x7E2 = one attachment; see #378 on why that is read as a count).
		// Creates a Mail to the recipient with the sender's bank item escrowed, removes it from the sender's bank, and
		// replies DispatchSendMailResult. Rejects (success=0) an unknown recipient so the client shows "couldn't send".
		private async Task HandleSendMail(INetworkClient client, Rpc packet, FaolanStream body)
		{
			var character = client.Session.Character;

			var recipientName = body.ReadString();
			var bodyText = body.ReadString();
			var money = body.ReadUInt64();
			// The attachment list (#378). The word before the slot is 0x7E2 in the one captured SendMail, and was long
			// read as a "category constant" on that basis - but 0x7E2 is exactly ListFrame.ForCount(1) and that mail
			// has exactly ONE attachment, so a single sample could never tell a constant from a 1-element count.
			//
			// It IS a count: a mail sent with no attachment crashed the old parser with EndOfStreamException here (it
			// read the slot unconditionally), which is the failure a count predicts and a constant does not. So parse
			// the whole thing DEFENSIVELY - every read is guarded, because this body is client-controlled and an
			// EndOfStream out of a packet handler drops the player's connection (that is exactly how it was found).
			var bankSlot = -1;
			if (body.Position + 4 <= body.Length)
			{
				var attachmentWord = body.ReadUInt32();
				if (!ListFrame.TryCountOf(attachmentWord, out var attachmentCount))
				{
					// Not count-framed => not the shape we think. Say so with the bytes rather than guessing.
					_logger.LogWarning("Tradepost mail: attachment word 0x{Word:X} is not count-framed; ignoring attachments (#378).", attachmentWord);
					attachmentCount = 0;
				}

				for (var i = 0; i < attachmentCount && body.Position + 4 <= body.Length; i++)
				{
					var slot = (int)body.ReadUInt32();
					if (i == 0)
						bankSlot = slot; // one escrowed item is all Mail.SenderBankSlot models today
				}

				if (attachmentCount > 1)
					_logger.LogWarning("Tradepost mail: {Char} sent {Count} attachments; only the first is escrowed (#378).", character.Name, attachmentCount);
			}

			var recipient = await _database.GetCharacterByName(recipientName, character.RealmId);
			if (recipient == null)
			{
				_logger.LogInformation("Tradepost mail: {Char} -> unknown recipient '{Recipient}'; rejected.", character.Name, recipientName);
				await TradepostResponses.SendMailSendResult(client, false, packet.Sender.Instance);
				return;
			}

			var now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

			var mail = new Mail
			{
				MailId = await AllocateMailId(),
				RealmId = recipient.RealmId, // #324: no cross-realm mail (recipient's realm)
				RecipientClass = MailConstants.EndpointClass,
				RecipientName = recipient.Name!,
				SenderClass = MailConstants.EndpointClass,
				SenderName = character.Name!, // a player mail, NOT a system mail (sender name is set)
				BodyXml = bodyText,
				FooterWord = MailConstants.FooterUnread,
				Money = money,
				Timestamp1 = now,
				Timestamp2 = now + MailConstants.ExpirySeconds
			};

			// Attach the sender's bank item (if a slot was given and an item sits there). The item STAYS in the
			// sender's bank with a "mail-pending" overlay (it does NOT leave on send) - it only leaves when the
			// recipient TAKES it (GetMailItem), and the overlay clears if the sender cancels the mail.
			var attached = await _database.GetCharacterItemSlot(character.Id, ContainerKind.Bank, bankSlot);
			if (attached != null)
			{
				mail.SenderBankSlot = (uint)bankSlot;
				mail.Attachments.Add(new MailAttachment
				{
					// Deterministic per-mail instance id (the escrow mail has one attachment); mirrors the purchase
					// mail's ItemInstanceId = listing.SaleId. Only needs to be unique within the mail's attachments -
					// TakeMailItem resolves the item by Template, not by this id.
					ItemInstanceId = mail.MailId,
					Quantity = attached.Count == 0
						? 1u
						: attached.Count,
					Template = attached.TemplateId, // the key lives in Items (FK on Template); no inline copy
					ItemLevel = attached.Template.Level,
					ItemHash = attached.Template.Hash,
					TrailerWord = MailConstants.AttachmentTrailerWord,
					TrailerClass = MailConstants.EndpointClass,
					TrailerTail = 0
				});
			}

			await _database.CreateMail(mail);
			await TradepostResponses.SendMailSendResult(client, true, packet.Sender.Instance);

			// Draw the mail-pending overlay on the kept bank item (capture F500: stat 0 = flags 0x8001).
			if (attached != null)
				await TradepostResponses.SendBankSlotStat(client, (uint)bankSlot, TradepostListingCodec.StatFlags, BankFlagMailPending);

			// Retail pushes a 0x2005 wallet refresh after SendMail too - again with NO postage fee (the captured
			// balance is unchanged across the send-mail-with-attachment capture), #182. Push the current balance so
			// the HUD stays in sync (reflects any attached money debit elsewhere; the item attachment itself is free).
			await _outbound.SendUpdateCash(client, character.Money, UpdateCashReason.Spend);

			_logger.LogInformation("Tradepost mail: {Char} sent mail 0x{Mail:X16} to {Recipient} (\"{Body}\", money {Money}, attach {HasItem}).", character.Name, mail.MailId, recipient.Name, bodyText,
				money, attached != null);
		}

		// GetMailItem: grant the escrowed attachment into the bag, then drop it from the mail and re-send the
		// now-itemless mail so the client clears the attachment. The item is RENDERED live from the attachment's
		// stored INSTANCE identity (Data quad + template + level + hash) - the same fields the AH grid renders from -
		// so it works even when the listed template isn't in our Items table (most AH listings reference retail
		// templates we never harvested, and listing.ItemHash/hash is 0). Persistence is conditional: only a real
		// buildable Items row can be written to CharacterInventory (the ItemId FK requires it) and re-rendered by the
		// world-entry snapshot, so an un-harvested item is a LIVE-ONLY delivery (kept this session, lost on relog).
		// Aborts (item left in the mail) only on a full bag, so the player can retry.
		private async Task TakeMailItem(INetworkClient client, ulong mailId, ulong itemInstanceId, uint inst)
		{
			var character = client.Session.Character;
			var mail = await _database.GetMailByWireId(mailId);
			var attachment = mail?.Attachments?.FirstOrDefault(a => a.ItemInstanceId == itemInstanceId) ?? mail?.Attachments?.FirstOrDefault();
			if (mail == null || attachment == null)
			{
				_logger.LogWarning("Tradepost mail: {Char} tried to take item 0x{Item:X16} from mail 0x{Mail:X16} (no such attachment).", character.Name, itemInstanceId, mailId);
				return;
			}

			var qty = attachment.Quantity == 0
				? 1u
				: attachment.Quantity;

			// Delivered through the SHARED grant, not a private copy of it (#720). This used to find its own bag
			// slot, build its own wire entry and write its own CharacterInventory row - which meant an item bought
			// from the auction house and taken from mail did NOT advance a collect objective, while the same item
			// looted, bought from a vendor, harvested, claimed or `.additem`ed did. `GrantItemToBackpack` ends with
			// `AdvanceCollectGoals`; its comment calling itself "the one choke point every grant passes through"
			// only became true here.
			//
			// It also picks up two behaviours this path never had: a QUEST item arriving by mail now routes to the
			// quest tab rather than the backpack, and the bag capacity accounts for the equipped bag's extra slots.
			//
			// The attachment's captured `ItemLevel` is passed because `Items.Level` cannot supply it yet (#725).
			var slot = await _outbound.GrantItemToBackpack(client, attachment.Template, qty, level: attachment.ItemLevel);
			if (slot == null)
			{
				_logger.LogWarning("Tradepost mail: {Char}'s bag is full; mail 0x{Mail:X16} item kept in mail.", character.Name, mailId);
				return;
			}
			// The grant PERSISTS as well as delivering, so there is no SetCharacterInventorySlot here. There used to
			// be, and after the routing above it would have been a double write - into ContainerKind.Bag even when
			// the grant had routed a quest item to the quest tab, leaving the same item in two containers.
			//
			// It was also conditional: a purchase whose template had no harvested hash was delivered LIVE-ONLY,
			// because the world-entry snapshot rendered `Hash != 0` only and persisting one would have left an
			// invisible item holding a bag slot. That covered the ORDINARY auction-house case, not an edge - retail
			// reveals an item's hash only as it enters an inventory, so a listing and a mailbox both carry 0.
			// #723 removed the premise (the token is minted when none was harvested); the trace that established it
			// is written up in docs/subsystems/item-claims.md.

			// For a PLAYER mail, the attached item was still sitting in the SENDER's bank (with the mail-pending
			// overlay) - now that the recipient has taken it, remove it from the sender's bank so it isn't duplicated.
			// AH system mail has an empty sender (the listed item already left the seller's bank on buy), so skip it.
			if (!string.IsNullOrEmpty(mail.SenderName))
			{
				var sender = await _database.GetCharacterByName(mail.SenderName, mail.RealmId);
				if (sender != null)
					await _database.SetCharacterItemSlot(sender.Id, ContainerKind.Bank, (int)mail.SenderBankSlot, null);
			}

			await _database.RemoveMailAttachment(mailId, attachment.ItemInstanceId);
			mail.Attachments.Remove(attachment);
			// #213: also bank any attached money on the take so the re-sent mail shows NO remaining attachment - the
			// client greys out delete while a mail still has unclaimed contents, so leaving the money behind is what
			// pinned the delete button (and "take all" never credited the tin). Retail captures never carry mail money,
			// so there's no observed take-money op; crediting it with the item take matches the player expectation.
			await GrantMailMoney(client, mail);
			await TradepostResponses.SendMailMessage(client, mail, false, inst);
			// Complete the take with a result ack, mirroring MarkMailRead (#213): without it the client's mail-op state
			// machine stays mid-take, so the FIRST delete click after a retrieve is swallowed (the mail survives until the
			// mailbox is reopened, which resets that state); the ack lets the delete go through on the first try.
			await TradepostResponses.SendMailResult(client, true, 0, inst);

			_logger.LogInformation("Tradepost mail: {Char} took item (template {Template}) from mail 0x{Mail:X16} into bag slot {Slot}.", character.Name, attachment.Template, mailId, slot);
		}

	}
}
