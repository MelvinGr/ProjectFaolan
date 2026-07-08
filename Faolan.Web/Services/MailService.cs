using Faolan.Core;
using Faolan.Core.Database;
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

		public MailService(IDbContextFactory<DatabaseContext> dbf)
		{
			_dbf = dbf;
		}

		public async Task<List<MailView>> GetMailboxAsync(string characterName)
		{
			await using var db = await _dbf.CreateDbContextAsync();
			var mails = await db.Mails.Where(m => m.RecipientName == characterName).Include(m => m.Attachments).OrderByDescending(m => m.Timestamp1).ToListAsync();

			var templateIds = mails.SelectMany(m => m.Attachments).Select(a => a.Template).Distinct().ToList();
			var names = await db.Items.Where(i => templateIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.Name);

			return mails
				.Select(m => new MailView(m.MailId,
					string.IsNullOrEmpty(m.SenderName)
						? "(system)"
						: m.SenderName,
					m.BodyXml ?? "",
					m.Money,
					m.IsRead,
					DateTimeOffset.FromUnixTimeSeconds(m.Timestamp1),
					m
						.Attachments
						.Select(a => new MailItemView(names.TryGetValue(a.Template, out var n) && !string.IsNullOrWhiteSpace(n)
								? n
								: $"Item {a.Template}",
							a.Quantity))
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

		// Compose a mail. money/item attachments are GM-style (minted, not deducted from the sender) — gate them to
		// admins at the call site. Returns (ok, message).
		public async Task<(bool Ok, string Message)> SendAsync(string recipientName, string senderName, string body, ulong moneyTin = 0, uint itemId = 0, uint count = 1)
		{
			if (string.IsNullOrWhiteSpace(recipientName))
				return (false, "Enter a recipient.");

			await using var db = await _dbf.CreateDbContextAsync();
			var recipient = await db.Characters.FirstOrDefaultAsync(c => c.Name == recipientName);
			if (recipient == null)
				return (false, $"No character named '{recipientName}'.");

			var now = Global.NowUnixTimeSeconds;
			var mail = new Mail
			{
				MailId = (ulong)Random.Shared.NextInt64(1, long.MaxValue),
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

			if (itemId != 0)
			{
				var item = await db.Items.FirstOrDefaultAsync(i => i.Id == itemId);
				if (item == null)
					return (false, $"No item with id {itemId} (nothing sent).");

				mail.Attachments.Add(new MailAttachment
				{
					ItemInstanceId = (ulong)Random.Shared.NextInt64(1, long.MaxValue),
					Quantity = count == 0
						? 1u
						: count,
					Data1 = item.Data1,
					Data2 = item.Data2,
					Data3 = item.Data3,
					Data4 = item.Data4,
					Template = item.Id,
					ItemLevel = item.Level,
					ItemHash = item.Hash,
					TrailerWord = MailConstants.AttachmentTrailerWord,
					TrailerClass = MailConstants.EndpointClass,
					TrailerTail = 0
				});
			}

			db.Mails.Add(mail);
			await db.SaveChangesAsync();
			return (true, $"Mail sent to {recipient.Name}.");
		}

		public async Task<int> UnreadCountAsync(string characterName)
		{
			await using var db = await _dbf.CreateDbContextAsync();
			return await db.Mails.CountAsync(m => m.RecipientName == characterName && !m.IsRead);
		}
	}
}
