using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Repositories
{
	public interface IEconomyRepository
	{
		Task CreateMail(Mail mail);
		Task CreateTradepostListing(TradepostListing listing);
		Task DeleteMail(ulong mailId);
		Task DeleteTradepostListing(ulong saleId, uint realmId);
		Task<Mail?> GetMailByWireId(ulong mailId);
		Task<Mail[]> GetMailsForRecipient(string recipientName, uint realmId);
		Task<ulong> GetNextMailId();
		Task<ulong> GetNextTradepostSaleId(uint realmId);
		Task<TradepostListing?> GetTradepostListingBySaleId(ulong saleId, uint realmId);
		Task<TradepostListing?> GetTradepostListingBySellerSlot(string sellerName, uint bankSlot, uint realmId);
		Task<TradepostListing[]> GetTradepostListings(uint itemId, uint realmId);
		Task RemoveMailAttachment(ulong mailId, ulong itemInstanceId);
		Task SetMailMoney(ulong mailId, ulong money);
		Task SetMailRead(ulong mailId, bool read);
		Task SetTradepostListingPrice(ulong saleId, uint realmId, ulong price);
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

		public async Task DeleteTradepostListing(ulong saleId, uint realmId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx.TradepostListings.Where(l => l.RealmId == realmId && l.SaleId == saleId).ExecuteDeleteAsync();
		}

		public async Task<Mail?> GetMailByWireId(ulong mailId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Mails
				.Include(m => m.Attachments).ThenInclude(a => a.Item)
				.Include(m => m.Attachments).ThenInclude(a => a.Escrowed!).ThenInclude(i => i.Template)
				.Include(m => m.Attachments).ThenInclude(a => a.Escrowed!).ThenInclude(i => i.Sockets)
				.AsSplitQuery()
				.FirstOrDefaultAsync(m => m.MailId == mailId);
		}

		public async Task<Mail[]> GetMailsForRecipient(string recipientName, uint realmId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			// Newest first by send time: Timestamp1 is the send time our own mail builders stamp. Tie-break on
			// MailId so same-second mails still order deterministically.
			//
			// Ordered in memory, like GetTradepostListingsForItem above: SQLite cannot order by a ulong (its integer is
			// signed 64-bit) and throws NotSupportedException on the MailId tie-break. An inbox is small, so the sort
			// is free; the WHERE still runs in SQL.
			var rows = await ctx.Mails.Include(m => m.Attachments).ThenInclude(a => a.Item).Where(m => m.RealmId == realmId && m.RecipientName == recipientName).ToListAsync();
			return rows.OrderByDescending(m => m.Timestamp1).ThenByDescending(m => m.MailId).ToArray();
		}

		// The next wire MailId: max + 1 (MailId is the global PK). A deterministic sequence, not a random id +
		// collision retry. Computed in memory because SQLite cannot MAX a ulong (its integer is signed 64-bit).
		public async Task<ulong> GetNextMailId()
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var mailIds = await ctx.Mails.Select(m => m.MailId).ToListAsync();
			return mailIds.Count == 0
				? 1UL
				: mailIds.Max() + 1UL;
		}

		// The next sale id for a new listing in this realm: max in the realm + 1 (SaleId is realm-unique). A
		// deterministic sequence, not a random id + collision retry. Computed in memory because SQLite cannot MAX a
		// ulong (its integer is signed 64-bit); a realm's listing set is small, so the load is cheap.
		public async Task<ulong> GetNextTradepostSaleId(uint realmId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var saleIds = await ctx.TradepostListings.Where(l => l.RealmId == realmId).Select(l => l.SaleId).ToListAsync();
			return saleIds.Count == 0
				? 1UL
				: saleIds.Max() + 1UL;
		}

		public async Task<TradepostListing?> GetTradepostListingBySaleId(ulong saleId, uint realmId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.TradepostListings.FirstOrDefaultAsync(l => l.RealmId == realmId && l.SaleId == saleId);
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

		// MailAttachment.MailId is the wire id (FK -> Mail.MailId, Mail's PK), so this compares like for like. Both
		// sides must stay the wire id: a narrower row key on one side widens silently at the comparison, matches
		// nothing, and leaves the taken item's attachment behind to come back as a dupe on relog.
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

		public async Task SetTradepostListingPrice(ulong saleId, uint realmId, ulong price)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx.TradepostListings.Where(l => l.RealmId == realmId && l.SaleId == saleId).ExecuteUpdateAsync(s => s.SetProperty(l => l.Price, price));
		}
	}
}
