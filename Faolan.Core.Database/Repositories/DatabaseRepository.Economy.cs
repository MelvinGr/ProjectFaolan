using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Repositories
{
	public partial interface IDatabaseRepository
	{
		Task CreateMail(Mail mail);
		Task CreateTradepostListing(TradepostListing listing);
		Task DeleteMail(ulong mailId);
		Task DeleteTradepostListing(ulong saleId);
		Task<Mail?> GetMailByWireId(ulong mailId);
		Task<Mail[]> GetMailsForRecipient(string recipientName, uint realmId);
		Task<TradepostListing?> GetTradepostListingBySaleId(ulong saleId);
		Task<TradepostListing?> GetTradepostListingBySellerSlot(string sellerName, uint bankSlot, uint realmId);
		Task<TradepostListing[]> GetTradepostListings(uint itemId, uint realmId);
		Task RemoveMailAttachment(ulong mailId, ulong itemInstanceId);
		Task SetMailMoney(ulong mailId, ulong money);
		Task SetMailRead(ulong mailId, bool read);
		Task SetTradepostListingPrice(ulong saleId, ulong price);
	}

	public partial class DatabaseRepository
	{
		public async Task CreateMail(Mail mail)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			ctx.Mails.Add(mail);
			await ctx.SaveChangesAsync();
		}

		public async Task CreateTradepostListing(TradepostListing listing)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			ctx.TradepostListings.Add(listing);
			await ctx.SaveChangesAsync();
		}

		public async Task DeleteMail(ulong mailId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx.Mails.Where(m => m.MailId == mailId).ExecuteDeleteAsync();
		}

		public async Task DeleteTradepostListing(ulong saleId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx.TradepostListings.Where(l => l.SaleId == saleId).ExecuteDeleteAsync();
		}

		public async Task<Mail?> GetMailByWireId(ulong mailId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Mails.Include(m => m.Attachments).ThenInclude(a => a.Item).FirstOrDefaultAsync(m => m.MailId == mailId);
		}

		public async Task<Mail[]> GetMailsForRecipient(string recipientName, uint realmId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			// Newest first by SEND TIME. This used to lean on the surrogate Id's autoincrement as an insertion-order
			// proxy; with MailId as the PK (#368) that proxy is gone and MailId itself is useless for it (AllocateMailId
			// is Random.Shared, not monotonic). Timestamp1 is the send time our own mail builders stamp. Tie-break on
			// MailId so same-second mails still order deterministically.
			//
			// Ordered in MEMORY, like GetTradepostListingsForItem above: SQLite cannot ORDER BY a ulong (its INTEGER is
			// signed 64-bit) and throws NotSupportedException on the MailId tie-break. An inbox is small, so the sort
			// is free; the WHERE still runs in SQL.
			var rows = await ctx.Mails.Include(m => m.Attachments).ThenInclude(a => a.Item).Where(m => m.RealmId == realmId && m.RecipientName == recipientName).ToListAsync();
			return rows.OrderByDescending(m => m.Timestamp1).ThenByDescending(m => m.MailId).ToArray();
		}

		public async Task<TradepostListing?> GetTradepostListingBySaleId(ulong saleId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.TradepostListings.FirstOrDefaultAsync(l => l.SaleId == saleId);
		}

		public async Task<TradepostListing?> GetTradepostListingBySellerSlot(string sellerName, uint bankSlot, uint realmId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.TradepostListings.FirstOrDefaultAsync(l => l.RealmId == realmId && l.SellerName == sellerName && l.BankSlot == bankSlot);
		}

		public async Task<TradepostListing[]> GetTradepostListings(uint itemId, uint realmId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var rows = await ctx.TradepostListings.Include(l => l.TemplateItem).Where(l => l.RealmId == realmId && l.ItemId == itemId).ToListAsync();
			return rows.OrderBy(l => l.Price).ToArray();
		}

		// MailAttachment.MailId is the WIRE id (FK -> Mail.MailId, Mail's PK), so this compares like for like. It did
		// NOT before #368: the FK was a uint pointing at a surrogate Mail.Id row key while callers passed the ulong
		// wire id, which widened and silently matched nothing - the delete was a no-op, the taken item's attachment
		// survived, and it came back on relog as a dupe (masked in-session by TakeMailItem's Attachments.Remove).
		public async Task RemoveMailAttachment(ulong mailId, ulong itemInstanceId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx.MailAttachments.Where(a => a.MailId == mailId && a.ItemInstanceId == itemInstanceId).ExecuteDeleteAsync();
		}

		public async Task SetMailMoney(ulong mailId, ulong money)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx.Mails.Where(m => m.MailId == mailId).ExecuteUpdateAsync(s => s.SetProperty(m => m.Money, money));
		}

		public async Task SetMailRead(ulong mailId, bool read)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx.Mails.Where(m => m.MailId == mailId).ExecuteUpdateAsync(s => s.SetProperty(m => m.IsRead, read));
		}

		public async Task SetTradepostListingPrice(ulong saleId, ulong price)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx.TradepostListings.Where(l => l.SaleId == saleId).ExecuteUpdateAsync(s => s.SetProperty(l => l.Price, price));
		}
	}
}
