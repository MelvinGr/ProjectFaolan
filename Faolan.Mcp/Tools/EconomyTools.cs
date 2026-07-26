using System;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Network.Constants;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace Faolan.Mcp.Tools
{
	/// <summary>
	///     Auction-house (Tradepost) + mail inspection and authoring over faolan.db: browse listings, read a
	///     character's mailbox, and create listings / send mail (DB-backed; the recipient/searcher sees them the
	///     next time they open the AH or mailbox in-game). Money is in tin (the smallest coin; 100 tin = 1 copper,
	///     1,000,000 tin = 1 gold). Item identity uses faolan.db item ids (see search_items / get_item).
	/// </summary>
	[McpServerToolType]
	public sealed class EconomyTools
	{
		private static readonly JsonSerializerOptions JsonOptions = new()
		{
			WriteIndented = true,
			DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
		};

		private readonly IDatabaseRepository _db;

		public EconomyTools(IDatabaseRepository db)
		{
			_db = db;
		}


		[McpServerTool(Name = "create_tradepost_listing"),
		 Description(
			 "Create an auction-house listing for an item (by faolan.db item id) at a price in tin, on behalf of a seller name. DB-backed - it shows up when a player searches that item id in-game. Unlike imported listings, the ItemId and Template are both the item, so display = delivery. Find item ids with search_items.")]
		public async Task<string> CreateTradepostListing([Description("Item id to list (see search_items / get_item)")] uint itemId, [Description("Asking price in tin")] ulong priceTin,
			[Description("Quantity in the stack (default 1)")] uint quantity = 1, [Description("Seller name shown in the listing (default \"Claude\")")] string sellerName = "Claude",
			[Description("Realm to list in (0 = the only configured realm; required when there is more than one)")] uint realmId = 0)
		{
			var item = await _db.GetItem(itemId);
			if (item == null)
				return $"No item with id {itemId}.";

			// Same realm FK as mail below. The seller is a NAME here rather than a character, so there is no character to
			// take the realm from - with one realm that is unambiguous, with several the caller has to say which.
			var realms = await _db.GetRealms();
			if (realmId == 0 && realms.Length == 1)
				realmId = realms[0].Id;

			if (realms.All(r => r.Id != realmId))
			{
				return $"realmId {realmId} is not a configured realm; pass one of: " +
				       string.Join(", ", realms.Select(r => $"{r.Id} ({r.Name})")) + ".";
			}

			var listing = new TradepostListing
			{
				RealmId = realmId,
				// The realm's next sale id, the same allocator the in-game listing path uses - NOT a random id. The
				// unique key is (RealmId, SaleId), so a random value collides eventually and is unordered meanwhile.
				SaleId = await _db.GetNextTradepostSaleId(realmId),
				Handle = 0,
				SellerName = sellerName ?? "Claude",
				BankSlot = 0,
				Price = priceTin,
				Quantity = quantity == 0
					? 1u
					: quantity,
				ItemId = itemId,
				InstanceField = 0,
				ItemHandle = 0,
				EntryFlags = TradepostListingCodec.DefaultEntryFlags,
				Timestamp = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
				Template = item.Id, // the key lives in Items (FK on Template); no inline copy
				ItemLevel = item.Level,
				ItemHash = item.Hash
			};

			await _db.CreateTradepostListing(listing);
			return
				$"Listed {listing.Quantity}x \"{item.Name}\" (id {itemId}) for {priceTin} tin by \"{listing.SellerName}\" (saleId 0x{listing.SaleId:X16}) in realm {realmId}. Searchable under itemId {itemId} in-game.";
		}

		[McpServerTool(Name = "get_mailbox"),
		 Description(
			 "Read a character's in-game mailbox from faolan.db (newest first): sender, read flag, attached money, body, and item attachments (template + quantity). Read-only. Character ids: see list_world_sessions / list_sessions / get_character.")]
		public async Task<string> GetMailbox([Description("Numeric character id of the mailbox owner")] uint characterId)
		{
			var character = await _db.GetCharacter(characterId);
			if (character?.Name == null)
				return $"No character with id {characterId}.";

			var mails = await _db.GetMailsForRecipient(character.Name, character.RealmId);
			return JsonSerializer.Serialize(new
			{
				Owner = character.Name,
				mails.Length,
				Mails = mails.Select(m => new
				{
					MailId = "0x" + m.MailId.ToString("X16"),
					From = string.IsNullOrEmpty(m.SenderName)
						? "(system)"
						: m.SenderName,
					m.IsRead,
					MoneyTin = m.Money,
					Body = m.BodyXml,
					Attachments = m.Attachments.Select(a => new
					{
						a.Template,
						a.Quantity,
						ItemInstanceId = "0x" + a.ItemInstanceId.ToString("X16")
					})
				})
			}, JsonOptions);
		}

		[McpServerTool(Name = "list_tradepost_listings"),
		 Description(
			 "Browse auction-house (Tradepost) listings from faolan.db, cheapest first. Filter by itemId (the search/category id rows are filed under) and/or sellerName; both optional. Read-only. Note: a listing's ItemId is the search category and its Template is the actual item delivered.")]
		public async Task<string> ListTradepostListings([Description("Optional: only listings filed under this itemId (0 = any)")] uint itemId = 0,
			[Description("Optional: only listings by this exact seller name")] string? sellerName = null, [Description("Max results (default 25)")] int limit = 25)
		{
			var rows = await _db.Query(ctx => ctx.TradepostListings.Where(l => (itemId == 0 || l.ItemId == itemId) && (sellerName == null || l.SellerName == sellerName)).Take(1000).ToListAsync());

			var listings = rows
				.OrderBy(l => l.Price)
				.Take(Math.Max(1, limit))
				.Select(l => new
				{
					SaleId = "0x" + l.SaleId.ToString("X16"),
					l.SellerName,
					PriceTin = l.Price,
					l.Quantity,
					l.ItemId,
					l.Template,
					l.BankSlot
				});

			return JsonSerializer.Serialize(new
			{
				rows.Count,
				Listings = listings
			}, JsonOptions);
		}

		[McpServerTool(Name = "send_mail"),
		 Description(
			 "Send an in-game mail to a character (DB-backed - it appears the next time they open their mailbox). Optionally attach money (tin) and/or one item by faolan.db item id. The sender name defaults to \"Claude\". Use this to deliver items/coin to players. Find item ids with search_items.")]
		public async Task<string> SendMail([Description("Numeric character id of the recipient")] uint recipientCharacterId, [Description("The mail body text")] string body,
			[Description("Optional money to attach, in tin (default 0)")] ulong moneyTin = 0, [Description("Optional item id to attach (0 = none; see search_items)")] uint itemId = 0,
			[Description("Attached item stack count (default 1)")] uint count = 1, [Description("Sender name shown to the recipient (default \"Claude\")")] string senderName = "Claude")
		{
			var recipient = await _db.GetCharacter(recipientCharacterId);
			if (recipient?.Name == null)
				return $"No character with id {recipientCharacterId}.";

			var now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

			var mail = new Mail
			{
				// MailId IS the wire id and the primary key (#368), so it comes from the repository's allocator - the
				// same one the in-game mail path uses - rather than from a random draw.
				MailId = await _db.GetNextMailId(),
				// Mail is realm-scoped (#324) and RealmId is an FK to Realms, so leaving it at the default 0 makes the
				// insert fail on the foreign key. It is the RECIPIENT's realm - the mailbox read path pairs the same two
				// columns (GetMailsForRecipient(name, realmId)).
				RealmId = recipient.RealmId,
				RecipientClass = MailConstants.EndpointClass,
				RecipientName = recipient.Name,
				SenderClass = MailConstants.EndpointClass,
				SenderName = senderName ?? "",
				BodyXml = body ?? "",
				FooterWord = MailConstants.FooterUnread,
				Money = moneyTin,
				Timestamp1 = now,
				Timestamp2 = now + MailConstants.ExpirySeconds
			};

			string? itemNote = null;
			if (itemId != 0)
			{
				var item = await _db.GetItem(itemId);
				if (item == null)
					return $"No item with id {itemId} (the rest of the mail was not sent).";

				mail.Attachments.Add(new MailAttachment
				{
					// Only has to be unique among this mail's attachments, so the mail id serves - the same choice the
					// server's mail path makes (GameServerListener.Tradepost.cs).
					ItemInstanceId = mail.MailId,
					Quantity = count == 0
						? 1u
						: count,
					Template = item.Id, // the key lives in Items (FK on Template); no inline copy
					ItemLevel = item.Level,
					ItemHash = item.Hash,
					TrailerWord = MailConstants.AttachmentTrailerWord,
					TrailerClass = MailConstants.EndpointClass,
					TrailerTail = 0
				});
				itemNote = $"{count}x \"{item.Name}\" (id {itemId})";
			}

			await _db.CreateMail(mail);
			return $"Sent mail 0x{mail.MailId:X16} to {recipient.Name} from \"{mail.SenderName}\"" + (moneyTin > 0
				? $", {moneyTin} tin"
				: "") + (itemNote != null
				? $", attachment {itemNote}"
				: "") + ". It appears when they next open their mailbox in-game.";
		}
	}
}
