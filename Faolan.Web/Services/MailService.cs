using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Constants;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Web.Services
{
	public sealed record MailItemView(string Name, uint Qty);

	public sealed record MailView(ulong MailId, string Sender, string Body, ulong Money, bool IsRead, DateTimeOffset Sent, IReadOnlyList<MailItemView> Items);

	// Read/compose in-game mail directly against faolan.db (the same table the game server's Tradepost mail uses).
	// DB-backed, so it works whether or not the game server is running; new mail appears when the recipient next
	// opens their in-game mailbox.
	public sealed class MailService
	{
		private readonly IDbContextFactory<DatabaseContext> _dbf;
		private readonly IEconomyRepository _economy;

		public MailService(IDbContextFactory<DatabaseContext> dbf, IEconomyRepository economy)
		{
			_dbf = dbf;
			_economy = economy;
		}

		public async Task<List<MailView>> GetMailboxAsync(string characterName)
		{
			await using var db = await _dbf.CreateDbContextAsync();
			var mails = await db.Mails.Where(m => m.RecipientName == characterName).Include(m => m.Attachments).OrderByDescending(m => m.Timestamp1).ToListAsync();

			var templateIds = mails.SelectMany(m => m.Attachments).Select(a => a.Template).Distinct().ToList();
			var names = await db.Items.Where(i => templateIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.Name);

			return mails
				.Select(m => new MailView(m.MailId, string.IsNullOrEmpty(m.SenderName)
					? "(system)"
					: m.SenderName, m.BodyXml ?? "", m.Money, m.IsRead, DateTimeOffset.FromUnixTimeSeconds(m.Timestamp1), m
					.Attachments.Select(a => new MailItemView(names.TryGetValue(a.Template, out var n) && !string.IsNullOrWhiteSpace(n)
						? n
						: $"Item {a.Template}", a.Quantity))
					.ToList()))
				.ToList();
		}

		public async Task MarkReadAsync(ulong mailId)
		{
			await using var db = await _dbf.CreateDbContextAsync();
			var m = await db.Mails.FirstOrDefaultAsync(x => x.MailId == mailId);
			if (m is { IsRead: false })
			{
				m.IsRead = true;
				await db.SaveChangesAsync();
			}
		}

		// Compose a mail. Money and item attachments are GM-style - minted rather than deducted from the sender - so
		// they must be gated to admins at the call site. Returns (ok, message).
		public async Task<(bool Ok, string Message)> SendAsync(string recipientName, string senderName, string body, ulong moneyTin = 0, uint itemId = 0, uint count = 1)
		{
			if (string.IsNullOrWhiteSpace(recipientName))
				return (false, "Enter a recipient.");

			await using var db = await _dbf.CreateDbContextAsync();
			var recipient = await db.Characters.FirstOrDefaultAsync(c => c.Name == recipientName);
			if (recipient == null)
				return (false, $"No character named '{recipientName}'.");

			var now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
			var mail = new Mail
			{
				// Mail is realm-scoped and RealmId is a foreign key to Realms: the recipient's realm, as the mailbox
				// read path pairs it (GetMailsForRecipient(name, realmId)).
				RealmId = recipient.RealmId,
				RecipientClass = MailConstants.EndpointClass,
				RecipientName = recipient.Name ?? "",
				SenderClass = MailConstants.EndpointClass,
				SenderName = senderName ?? "",
				BodyXml = body ?? "",
				FooterWord = MailConstants.FooterUnread,
				Money = moneyTin,
				Timestamp1 = now,
				Timestamp2 = now + MailConstants.ExpirySeconds
			};

			if (itemId != 0)
			{
				var item = await db.Items.FirstOrDefaultAsync(i => i.Id == itemId);
				if (item == null)
					return (false, $"No item with id {itemId} (nothing sent).");

				mail.Attachments.Add(new MailAttachment
				{
					Quantity = count == 0
						? 1u
						: count,
					Template = item.Id, // the key lives in Items (FK on Template); no inline copy
					ItemHash = item.Hash,
					TrailerWord = MailConstants.AttachmentTrailerWord,
					TrailerClass = MailConstants.EndpointClass,
					TrailerTail = 0
				});
			}

			// The game's allocator, not a random draw: MailId is the wire id and the primary key. An attachment's
			// instance id only has to be unique among this mail's attachments, so the mail id serves (TradepostService).
			await _economy.CreateMail(mailId =>
			{
				mail.MailId = mailId;
				foreach (var attachment in mail.Attachments)
					attachment.ItemInstanceId = mailId;

				return mail;
			});
			return (true, $"Mail sent to {recipient.Name}.");
		}

		public async Task<int> UnreadCountAsync(string characterName)
		{
			await using var db = await _dbf.CreateDbContextAsync();
			return await db.Mails.CountAsync(m => m.RecipientName == characterName && !m.IsRead);
		}
	}
}
