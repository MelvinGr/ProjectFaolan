using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Network.Server.CsPlayerAgent;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Economy
{
	/// <summary>
	///     The auction house (Tradepost) and the mailbox: the listing and mail transactions, plus the
	///     auctioneer-template cache.
	/// </summary>
	/// <remarks>
	///     Decoded from the NecroL80 sell, buy and mail captures (docs/subsystems/tradepost-protocol.md). The two
	///     cross-cutting outbound ops it needs - the wallet update and the live item grant - are reached through the
	///     <see cref="IEconomyOutbound" /> port, as <see cref="ShopService" /> and <see cref="BankService" /> do,
	///     while everything that is purely a send lives in <see cref="TradepostResponses" />.
	///     <see cref="Handle" /> returns false for a method it does not own, so the listener keeps ownership of the
	///     unknown-packet tracer, which is connection-level bookkeeping rather than economy logic.
	/// </remarks>
	public sealed class TradepostService
	{
		// The bank-slot overlay flag values the client draws a listed or mail-pending cell from (captures
		// F376/F398/F500).
		private const uint BankFlagListed = 0x401; // flags value while listed for sale

		private const uint BankFlagMailPending = 0x8001; // flags value while attached to an outgoing mail
		private const uint BankFlagNormal = 0x001; // flags value once unlisted / mail cancelled

		private readonly IDatabaseRepository _database;
		private readonly ILogger _logger;
		private readonly IEconomyOutbound _outbound;

		// The NPC templates that operate an auction house; talking to one opens the Tradepost, through the listener's
		// TalkToNpc auctioneer branch.
		//
		// The role comes from the client's own spawn data (`NpcStats.profession_type` -> `NpcStatCore.ProfessionType`)
		// rather than from a display name. A name would be the wrong key twice over: "Trader" is a role that four
		// distinct templates play, so it pins none of them, and any other NPC carrying the string would open the
		// auction house. The profession tag is exclusive - those same four templates are the only ones in the world
		// carrying it - so this recognises every auctioneer.
		private HashSet<uint>? _auctioneerTemplates;

		// The "you have mail" push rides the router tier (CSPlayerInterface) rather than this one - see
		// ICsPlayerAgentProvider.NotifyMailReceived - and is reached through the seam, like every other cross-listener
		// action.
		private readonly ICsPlayerAgentProvider _csPlayerAgent;

		private readonly BankService _bank;
		private readonly GuildBankService _guildBank;

		public TradepostService(IDatabaseRepository database, ILogger logger, IEconomyOutbound outbound, ICsPlayerAgentProvider csPlayerAgent, BankService bank, GuildBankService guildBank)
		{
			_bank = bank;
			_guildBank = guildBank;
			_database = database;
			_logger = logger;
			_outbound = outbound;
			_csPlayerAgent = csPlayerAgent;
		}

		public async Task<bool> IsAuctioneerTemplate(uint npcTemplateId)
		{
			_auctioneerTemplates ??= [.. await _database.GetNpcTemplatesByProfession(NpcProfession.Auctioneer)];
			return _auctioneerTemplates.Contains(npcTemplateId);
		}

		/// <summary>
		///     Inbound tradepost and mail dispatch, routed here by receiver class (TradepostAgentType). ItemSearch
		///     parses and replies through <see cref="TradepostResponses" />; the mutating ops are the methods below.
		///     Grammar in docs/subsystems/tradepost-protocol.md.
		/// </summary>
		/// <returns>False for a method this service does not own, so the caller can log it as unhandled.</returns>
		public async Task<bool> Handle(INetworkClient client, Rpc packet, FaolanStream body)
		{
			var method = (TradepostAgentMethods)packet.Method;
			switch (method)
			{
				case TradepostAgentMethods.UpdateItemTimeouts:
				{
					// Sent (empty body) after the client received DispatchOpenTradepost (auctioneer / .ah) - the
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

					// Always send the real result: an empty match list yields an empty grid, with no rows.
					await TradepostResponses.SendSearchResults(client, packet.Sender.Instance, listings);
					break;
				}

				case TradepostAgentMethods.BuyItem:
				{
					// Buy -> debit the buyer + deliver the item via a system mail.
					await HandleBuy(client, packet, body);
					break;
				}

				case TradepostAgentMethods.TradepostSellItem:
				{
					// Sell -> list a bank item for sale (creates a listing + escrows the item out of the bank).
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

					// Retail answers every close with its own: 11 for 11 over seven captures, with an empty body.
					await TradepostResponses.SendClose(client, packet.Sender.Instance);
					break;
				}

				// Mailbox retrieval ops: poll, preview, take the bought item, mark read, delete.
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

		/// <summary>
		///     Builds the system mail for a completed auction-house purchase: the buyer as recipient, an empty (system)
		///     sender, the bought item escrowed as an attachment, and the TradePostGUI_MailItemBought remoteformat as
		///     the body.
		/// </summary>
		/// <remarks>
		///     The item-name fragment is left empty, because the per-item RDB name key cannot be synthesised - see
		///     <c>MailColumns.ItemBoughtBody</c>. The item still delivers; only its name in the mail text degrades.
		/// </remarks>
		private static Mail BuildPurchaseMail(ulong mailId, string buyer, uint realmId, TradepostListing listing)
		{
			var now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

			var mail = new Mail
			{
				MailId = mailId,
				RealmId = realmId, // #324: no cross-realm mail (recipient = buyer)
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
				ItemHash = listing.ItemHash,
				TrailerWord = MailConstants.AttachmentTrailerWord,
				TrailerClass = MailConstants.EndpointClass,
				TrailerTail = 0
			});

			return mail;
		}

		/// <summary>
		///     Banks any money attached to a mail into the recipient's wallet and clears it from the mail.
		/// </summary>
		/// <remarks>
		///     Idempotent - a mail carrying no money is a no-op - so it is safe to call on every take. It has to happen
		///     on the take, because the client greys out delete while a mail still has unclaimed contents.
		/// </remarks>
		private async Task GrantMailMoney(INetworkClient client, Mail mail)
		{
			if (mail == null || mail.Money == 0)
				return;

			// One transaction clears the mail and credits the wallet, so the money cannot be banked twice (a failure
			// between two writes left it on the mail after crediting it), and the amount is the stored one, so a take
			// racing another take of the same mail moves it once.
			var character = client.Session.Character;
			var credited = await _database.ClaimMailMoney(mail.MailId, character.Id, character.Money);
			mail.Money = 0;
			if (credited == 0)
				return;

			character.Money += credited;
			await _outbound.SendUpdateCash(client, character.Money, UpdateCashReason.Gain);
			_logger.LogInformation("Tradepost mail: {Char} banked {Tin} tin from mail 0x{Mail:X16}.", character.Name, credited, mail.MailId);
		}

		/// <summary>
		///     TradepostBuyItem (0x200E) [saleId:u64][searchResultId:u64]: charges the buyer and delivers the listed
		///     item by system mail. Refuses on an unknown listing or insufficient funds, and no money moves until the
		///     mail is created.
		/// </summary>
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

			// The listed item stays in the seller's bank, under a "listed" overlay, until now. The sale escrows it to
			// the buyer: it moves out of the seller's bank into the buyer's Mail container (114), which is what retail
			// registers the instant a purchase completes. The row is handed over rather than deleted and re-minted, so
			// the crafter and every socketed gem travel with it.
			//
			// One transaction claims the listing, charges the buyer, escrows the row and writes the mail, so a second
			// buyer finds nothing to buy and a failure part-way leaves nothing half done.
			var seller = await _database.GetCharacterByName(listing.SellerName, character.RealmId);
			var balance = character.Money - listing.Price;
			var mail = await _database.CompleteTradepostPurchase(listing, character.Id, balance, seller?.Id, id => BuildPurchaseMail(id, character.Name!, character.RealmId, listing));
			if (mail == null)
			{
				_logger.LogInformation("Tradepost BUY: {Char} lost listing 0x{Sale:X16} to another buyer.", character.Name, saleId);
				return;
			}

			character.Money = balance;
			await _outbound.SendUpdateCash(client, character.Money, UpdateCashReason.Spend);

			// Push the mail with no attachment: it carries its own MailId, which the client then targets with the
			// GetMail ops to preview and take the item. The inbound TradepostInterface instance is echoed.
			await TradepostResponses.SendMailMessage(client, mail, false, packet.Sender.Instance);

			_logger.LogInformation("Tradepost BUY: {Char} bought listing 0x{Sale:X16} (item 0x{Item:X}) for {Price} tin -> mail 0x{Mail:X16} (wallet {Balance}).", character.Name, saleId,
				listing.ItemId, listing.Price, mail.MailId, character.Money);
		}

		/// <summary>
		///     CancelMail (0x2009) [mailId:u64]: recalls or deletes one of the player's mails (capture F403).
		/// </summary>
		/// <remarks>
		///     An escrowed item is returned before the mail goes, and the delete is refused when it cannot be: a bought
		///     item lives in the buyer's Mail container until they take it, so deleting the mail on top of it would
		///     strand a row nothing can reach, leaving the item paid for and gone. A player mail needs nothing - its
		///     attachment never left the sender's bank, and cancelling only clears the pending overlay.
		/// </remarks>
		private async Task HandleCancelMail(INetworkClient client, FaolanStream body)
		{
			var character = client.Session.Character;

			var mailId = body.ReadUInt64();

			// If the sender attached a bank item, clear its mail-pending overlay (capture F404: flag back to 1). The
			// item never left the bank, so cancelling frees it.
			var mail = await _database.GetMailByWireId(mailId);
			if (mail != null && mail.SenderName == character.Name && (mail.Attachments?.Count ?? 0) > 0)
				await TradepostResponses.SendBankSlotStat(client, mail.SenderBankSlot, TradepostListingCodec.StatFlags, BankFlagNormal);

			// Hand any escrowed row back to the canceller before the mail and its attachment rows go. Refusing on a
			// full bag is the safe side: the mail staying is recoverable, an unreachable item is not.
			if (mail?.Attachments?.FirstOrDefault(a => a.Escrowed != null) is { Escrowed: { } escrowed })
			{
				if (await _outbound.TransferItemToCharacter(character.Id, escrowed) == null)
				{
					_logger.LogInformation("Tradepost mail: {Char}'s bag is full; mail 0x{Mail:X16} kept so its escrowed item is not stranded.", character.Name, mailId);

					return;
				}
			}

			await _database.DeleteMail(mailId);
			_logger.LogInformation("Tradepost mail: {Char} cancelled/deleted mail 0x{Mail:X16}.", character.Name, mailId);
		}

		/// <summary>
		///     TradepostCancelSellItem (0x200C) [nBankItemSlot:u32]: pulls the seller's own listing (capture F397,
		///     slot 6).
		/// </summary>
		/// <remarks>
		///     The item never left the bank - it only carried the "listed" overlay - so cancelling deletes the listing
		///     and the client clears the overlay itself.
		/// </remarks>
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
			// Clear the bank-slot overlay (capture F398): the flag back to normal, the price to 0.
			await TradepostResponses.SendBankSlotStat(client, bankSlot, TradepostListingCodec.StatFlags, BankFlagNormal);
			await TradepostResponses.SendBankSlotStat(client, bankSlot, TradepostListingCodec.StatPrice, 0);
			_logger.LogInformation("Tradepost CANCELSELL: {Char} cancelled listing 0x{Sale:X16} (bank slot {Slot}); item stays in the bank.", character.Name, listing.SaleId, bankSlot);
		}

		/// <summary>
		///     TradepostChangeItemPrice (0x200B) [nBankItemSlot:u32][nNewPrice:u64]: reprices the seller's own listing,
		///     addressed by the origin bank slot (capture F363, slot 6 to 5,000,000 tin). There is no tradepost reply;
		///     the client re-polls.
		/// </summary>
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

		/// <summary>
		///     The mailbox ops: GetAllMails, GetMailByID, the GetItemData preview, the GetMailItem take, MarkMailRead
		///     and DeleteMail. All address the player's own mail by the wire MailId, and GetMailItem grants the
		///     escrowed item.
		/// </summary>
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
					// Preview: the mail with its attachment. The client shows the item in the mail; nothing is granted.
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

		/// <summary>
		///     TradepostSellItem (0x200A) [nBankItemSlot:u32][nPrice:u64]: lists one of the player's bank items
		///     (container 105) for sale, creating a <c>TradepostListing</c> from the bank item's identity and
		///     refreshing the bank tab.
		/// </summary>
		/// <remarks>Decoded from the NecroL80 sell_item_on_ah capture (F375, slot 6 at 1,000,000 tin).</remarks>
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

			// File the listing under the item's auction-house search category - the item-type-search id the client
			// queries by, 30030 for instance - so an ordinary item-type search surfaces it. The categories are
			// recovered from captured listings; an unknown one falls back to the template.
			var searchCategory = await _database.GetItemSearchCategory(listed.TemplateId) ?? listed.TemplateId;

			var listing = new TradepostListing
			{
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
				ItemHash = listed.Template.Hash
			};
			await _database.CreateTradepostListing(listing);

			// The listed item stays in its bank slot: the client draws a "listed" overlay when the server pushes these
			// three bank-slot stat updates (decoded from the capture, F376/F377) - price, timestamp and the listed
			// flag. It leaves the seller's bank only when someone buys it, at which point the sale clears the cell.
			await TradepostResponses.SendBankSlotStat(client, (uint)bankSlot, TradepostListingCodec.StatPrice, (uint)price);
			await TradepostResponses.SendBankSlotStat(client, (uint)bankSlot, TradepostListingCodec.StatTimestamp, (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
			await TradepostResponses.SendBankSlotStat(client, (uint)bankSlot, TradepostListingCodec.StatFlags, BankFlagListed);

			// Retail pushes a 0x2005 wallet refresh after a listing, with the balance unchanged: there is no listing
			// deposit or postage fee. Verified across the sell, send-mail, change-price and cancel captures, where the
			// seller stays at 1,365,148 tin throughout. So push the current balance, reason 0 as captured, and debit
			// nothing.
			await _outbound.SendUpdateCash(client, character.Money, UpdateCashReason.Spend);

			_logger.LogInformation("Tradepost SELL: {Char} listed \"{Item}\" x{Qty} (bank slot {Slot}, template {Template}) for {Price} tin -> sale 0x{Sale:X16}.", character.Name, listed.Template.Name,
				listing.Quantity, bankSlot, listed.TemplateId, price, listing.SaleId);
		}

		/// <summary>
		///     SendMail (0x2002) [u16+recipient][u16+body][money:u64][attachments: a list frame plus a u32 slot each]:
		///     a player-composed mail, optionally with a bank item attached.
		/// </summary>
		/// <remarks>
		///     It creates a <c>Mail</c> to the recipient with the sender's bank item escrowed, removes it from the
		///     sender's bank and replies DispatchSendMailResult. An unknown recipient is rejected with success=0, so
		///     the client shows "couldn't send". Captured at F499: one attachment in slot 5, no COD.
		/// </remarks>
		private async Task HandleSendMail(INetworkClient client, Rpc packet, FaolanStream body)
		{
			var character = client.Session.Character;

			var recipientName = body.ReadString();
			var bodyText = body.ReadString();
			var money = body.ReadUInt64();
			// The attachment list is count-framed. The word before the slot in the one captured SendMail is exactly the
			// list frame over one element, and that mail has exactly one attachment, so a single sample cannot tell a
			// constant from a 1-element count - but a mail sent with no attachment proves it, because reading the slot
			// unconditionally then runs off the end of the body.
			//
			// Every read here is guarded: the body is client-controlled, and an end-of-stream out of a packet handler
			// drops the player's connection.
			var bankSlot = -1;
			if (body.Position + 4 <= body.Length)
			{
				var attachmentWord = body.ReadUInt32();
				if (!ListFrame.TryCountOf(attachmentWord, out var attachmentCount))
				{
					// Not count-framed, so not the expected shape. Report the bytes rather than guessing.
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

			// Attach the sender's bank item, when a slot was given and an item sits there. The item stays in the
			// sender's bank under a "mail-pending" overlay rather than leaving on send: it leaves only when the
			// recipient takes it (GetMailItem), and the overlay clears if the sender cancels the mail.
			var attached = await _database.GetCharacterItemSlot(character.Id, ContainerKind.Bank, bankSlot);

			var mail = await _database.CreateMail(mailId =>
			{
				var built = new Mail
				{
					MailId = mailId,
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

				if (attached != null)
				{
					built.SenderBankSlot = (uint)bankSlot;
					built.Attachments.Add(new MailAttachment
					{
						// A deterministic per-mail instance id, mirroring the purchase mail's ItemInstanceId. It only
						// needs to be unique within the mail's attachments, because the take resolves the item by
						// template rather than by this id.
						ItemInstanceId = mailId,

						// The sender's own bank row, which stays in their bank until the recipient takes it - the escrow
						// here is the bank slot rather than a move. Recording it is what lets the take hand the row over
						// instead of minting a copy and losing the crafter and gems.
						EscrowedInstanceId = attached.Id,
						Quantity = attached.Count == 0
							? 1u
							: attached.Count,
						Template = attached.TemplateId, // the key lives in Items (FK on Template); no inline copy
						ItemHash = attached.Template.Hash,
						TrailerWord = MailConstants.AttachmentTrailerWord,
						TrailerClass = MailConstants.EndpointClass,
						TrailerTail = 0
					});
				}

				return built;
			});
			await TradepostResponses.SendMailSendResult(client, true, packet.Sender.Instance);

			// Tell the recipient. Retail's notification rides CSPlayerInterface, so it reaches them wherever they are
			// rather than only when they next open a mailbox. False means they are not connected to this realm's CS
			// tier right now, which is not an error: the mail is written either way.
			if (!await _csPlayerAgent.NotifyMailReceived(recipient.Id, mail.MailId))
				_logger.LogDebug("Tradepost mail: {Recipient} is not on the CS tier, no arrival notification sent.", recipient.Name);

			// Draw the mail-pending overlay on the kept bank item (capture F500: stat 0 is flags 0x8001).
			if (attached != null)
				await TradepostResponses.SendBankSlotStat(client, (uint)bankSlot, TradepostListingCodec.StatFlags, BankFlagMailPending);

			// Retail pushes a 0x2005 wallet refresh after SendMail too, again with no postage fee: the captured balance
			// is unchanged across the send-mail-with-attachment capture. Pushing the current balance keeps the HUD in
			// sync with any attached-money debit; the item attachment itself is free.
			await _outbound.SendUpdateCash(client, character.Money, UpdateCashReason.Spend);

			_logger.LogInformation("Tradepost mail: {Char} sent mail 0x{Mail:X16} to {Recipient} (\"{Body}\", money {Money}, attach {HasItem}).", character.Name, mail.MailId, recipient.Name, bodyText,
				money, attached != null);
		}

		/// <summary>
		///     GetMailItem: grants the escrowed attachment into the bag, then drops it from the mail and re-sends the
		///     now-itemless mail so the client clears the attachment.
		/// </summary>
		/// <remarks>
		///     The item is rendered live from the attachment's stored instance identity (Data quad, template, level and
		///     hash), the same fields the auction-house grid renders from, so it works even when the listed template is
		///     not in the Items table - most listings reference retail templates no capture harvested. Persistence
		///     needs a real Items row, because the inventory's foreign key requires one, so an un-harvested item is a
		///     live-only delivery, kept this session and lost on relog. The take aborts, leaving the item in the mail,
		///     only on a full bag, so the player can retry.
		/// </remarks>
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

			// Delivered through the shared grant rather than a private copy of it, so an item bought from the auction
			// house and taken from mail advances a collect objective like every other delivery, a quest item arriving
			// by mail routes to the quest tab, and the bag capacity accounts for the equipped bag's extra slots.
			//
			// The row is handed over where there is one, so the crafter and the socketed gems travel with it. Only an
			// importer-seeded listing's mail has no row, and that one mints from the template.
			var slot = attachment.Escrowed != null
				? await _outbound.TransferItemToCharacter(character.Id, attachment.Escrowed)
				: await _outbound.GrantItemToBackpack(client, attachment.Template, qty);
			if (slot == null)
			{
				_logger.LogWarning("Tradepost mail: {Char}'s bag is full; mail 0x{Mail:X16} item kept in mail.", character.Name, mailId);
				return;
			}
			// The grant persists as well as delivering, so nothing writes the inventory row a second time here: that
			// would be a double write, and into the bag even where the grant routed a quest item to the quest tab,
			// leaving the same item in two containers.
			//
			// It is also unconditional. Retail reveals an item's hash only as it enters an inventory, so a listing and
			// a mailbox both carry 0, and the token is minted where none was harvested - see
			// docs/subsystems/item-claims.md.

			// For a player mail the attached item was still sitting in the sender's bank, under the mail-pending
			// overlay, so now that the recipient has taken it, it is removed from the sender's bank rather than
			// duplicated. An auction-house system mail has an empty sender - the listed item left the seller's bank on
			// the buy - so it is skipped.
			//
			// Skipped entirely when the row was handed over: the transfer already moved that row out of the sender's
			// bank into the recipient's bag, so clearing the bank cell by (owner, container, slot) would match whatever
			// the sender has since put there and delete an innocent item.
			if (!string.IsNullOrEmpty(mail.SenderName) && attachment.Escrowed == null)
			{
				var sender = await _database.GetCharacterByName(mail.SenderName, mail.RealmId);
				if (sender != null)
					await _database.SetCharacterItemSlot(sender.Id, ContainerKind.Bank, (int)mail.SenderBankSlot, null);
			}

			await _database.RemoveMailAttachment(mailId, attachment.ItemInstanceId);
			mail.Attachments.Remove(attachment);
			// Bank any attached money on the take too, so the re-sent mail shows no remaining attachment: the client
			// greys out delete while a mail still has unclaimed contents, so money left behind pins the delete button
			// and never reaches the wallet. Retail captures never carry mail money, so there is no observed take-money
			// op; crediting it with the item take matches the player expectation.
			await GrantMailMoney(client, mail);
			await TradepostResponses.SendMailMessage(client, mail, false, inst);
			// Complete the take with a result ack, mirroring MarkMailRead: without it the client's mail-op state machine
			// stays mid-take, so the first delete click after a retrieve is swallowed and the mail survives until the
			// mailbox is reopened, which resets that state.
			await TradepostResponses.SendMailResult(client, true, 0, inst);

			_logger.LogInformation("Tradepost mail: {Char} took item (template {Template}) from mail 0x{Mail:X16} into bag slot {Slot}.", character.Name, attachment.Template, mailId, slot);
		}


		/// <summary>
		///     Bootstraps the auction-house window at <c>accessPosition</c>, which must be within the client's 15-unit
		///     proximity check. Shared by the <c>.ah</c> GM command and the retail trigger, talking to an auctioneer
		///     NPC.
		/// </summary>
		/// <returns>The bank item count, for the caller's feedback.</returns>
		/// <remarks>
		///     The window's default tab is the personal bank, a separate channel: container kind 105 delivered by a
		///     BankIIR, populated from the character's own container-105 rows.
		///     <para>
		///         Order matters. Retail sends the bank content - the 0x201c slot registrations and the BankIIR -
		///         before DispatchOpenTradepost, so the window opens already populated
		///         (account-faolan-...-Has-inventory-at-trader-bank F6208-6210 =
		///         [bank slot regs][BankIIR][DispOpenTradepost]). Sending the open first lands the BankIIR before the
		///         window exists, and the first open renders empty.
		///     </para>
		/// </remarks>
		internal async Task<int> SendAuctionHouseOpen(INetworkClient client, Vector3 accessPosition)
		{
			var characterId = client.Session.Character.Id;
			var stored = await _database.GetCharacterItems(characterId, ContainerKind.Bank);
			var bank = FullCharacterRoots.Container(ContainerKind.Bank, 50, 30, characterId, stored);

			await _bank.SendBank(client, bank);

			// The guild vault rides the same open: retail pushes DispatchGuildBankData in the same instant as the
			// personal bank's slot registrations and DispatchOpenTradepost, unasked - there is no OpenGuildBank request
			// on the wire at all. Before the open, for the same reason the bank is: a container that lands after the
			// window exists renders empty on the first open.
			await _guildBank.SendVault(client);
			await TradepostResponses.SendOpenTradepost(client, accessPosition);
			return bank.Items.Count;
		}
	}
}
