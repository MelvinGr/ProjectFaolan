using System;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Repositories
{
	public interface IEconomyRepository
	{
		/// <summary>
		///     Allocates the next wire MailId and stores the mail <paramref name="build" /> makes with it, in one write
		///     transaction, so two mails sent at once cannot draw the same id.
		/// </summary>
		Task<Mail> CreateMail(Func<ulong, Mail> build);

		/// <summary>
		///     Stores the listing under its realm's next sale id, allocated in the same write transaction.
		/// </summary>
		Task CreateTradepostListing(TradepostListing listing);

		/// <summary>
		///     Completes an auction-house purchase as one unit of work: claims the listing, sets the buyer's wallet to
		///     <paramref name="buyerMoney" />, moves the seller's listed row into the buyer's first free Mail cell, and
		///     stores the purchase mail <paramref name="build" /> makes.
		/// </summary>
		/// <returns>The stored mail, or null with nothing written when the listing is already gone (another buyer got it).</returns>
		/// <remarks>
		///     A listing with no seller character or no row in the seller's bank (an importer-seeded one) escrows
		///     nothing; its mail's take mints from the template.
		/// </remarks>
		Task<Mail?> CompleteTradepostPurchase(TradepostListing listing, uint buyerId, ulong buyerMoney, uint? sellerId, Func<ulong, Mail> build);
		Task DeleteMail(ulong mailId);
		Task DeleteTradepostListing(ulong saleId, uint realmId);
		Task<Mail?> GetMailByWireId(ulong mailId);
		Task<Mail[]> GetMailsForRecipient(string recipientName, uint realmId);
		Task<TradepostListing?> GetTradepostListingBySaleId(ulong saleId, uint realmId);
		Task<TradepostListing?> GetTradepostListingBySellerSlot(string sellerName, uint bankSlot, uint realmId);
		Task<TradepostListing[]> GetTradepostListings(uint itemId, uint realmId);
		Task RemoveMailAttachment(ulong mailId, ulong itemInstanceId);

		/// <summary>
		///     Moves a mail's money into the recipient's wallet in one transaction: clears it from the mail and writes
		///     <paramref name="wallet" /> plus that amount as the character's money.
		/// </summary>
		/// <returns>The amount moved; 0, with nothing written, when the mail carries none (already taken, or never had any).</returns>
		Task<ulong> ClaimMailMoney(ulong mailId, uint characterId, ulong wallet);
		Task SetMailRead(ulong mailId, bool read);
		Task SetTradepostListingPrice(ulong saleId, uint realmId, ulong price);
	}

	public partial class DatabaseRepository
	{
		// The allocations below are max + 1 read inside the transaction that inserts. On SQLite the transaction begins
		// IMMEDIATE, taking the write lock up front, so a second writer waits rather than reading the same max.
		public async Task<Mail> CreateMail(Func<ulong, Mail> build)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await using var tx = await ctx.Database.BeginTransactionAsync();
			var mail = build(await NextMailId(ctx));
			ctx.Mails.Add(mail);
			await ctx.SaveChangesAsync();
			await tx.CommitAsync();
			return mail;
		}

		public async Task CreateTradepostListing(TradepostListing listing)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await using var tx = await ctx.Database.BeginTransactionAsync();
			var saleIds = await ctx.TradepostListings.Where(l => l.RealmId == listing.RealmId).Select(l => l.SaleId).ToListAsync();
			listing.SaleId = saleIds.Count == 0
				? 1UL
				: saleIds.Max() + 1UL;
			ctx.TradepostListings.Add(listing);
			await ctx.SaveChangesAsync();
			await tx.CommitAsync();
		}

		public async Task<Mail?> CompleteTradepostPurchase(TradepostListing listing, uint buyerId, ulong buyerMoney, uint? sellerId, Func<ulong, Mail> build)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await using var tx = await ctx.Database.BeginTransactionAsync();

			// The claim: whoever deletes the row bought it. A second buyer's delete finds nothing and writes nothing.
			if (await ctx.TradepostListings.Where(l => l.RealmId == listing.RealmId && l.SaleId == listing.SaleId).ExecuteDeleteAsync() != 1)
				return null;

			await ctx.Characters.Where(c => c.Id == buyerId).ExecuteUpdateAsync(s => s.SetProperty(c => c.Money, buyerMoney));

			var mail = build(await NextMailId(ctx));

			// The escrow moves the row, so the crafter and the socketed gems travel with it. The first free Mail cell,
			// not a fixed one: a buyer can hold several untaken purchases, and a refused move used to fall through to
			// deleting the seller's row and minting a bare copy at the take.
			var listed = sellerId is { } seller
				? await ctx.ItemInstances.FirstOrDefaultAsync(i => i.OwnerCharacterId == seller && i.Container == ContainerKind.Bank && i.Slot == (int)listing.BankSlot)
				: null;
			if (listed != null)
			{
				var taken = await ctx.ItemInstances.Where(i => i.OwnerCharacterId == buyerId && i.Container == ContainerKind.Mail).Select(i => i.Slot).ToListAsync();
				var cell = 0;
				while (taken.Contains(cell))
					cell++;

				listed.OwnerCharacterId = buyerId;
				listed.Container = ContainerKind.Mail;
				listed.Slot = cell;
				mail.Attachments.First().EscrowedInstanceId = listed.Id;
			}

			ctx.Mails.Add(mail);
			await ctx.SaveChangesAsync();
			await tx.CommitAsync();
			return mail;
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

		public async Task<ulong> ClaimMailMoney(ulong mailId, uint characterId, ulong wallet)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await using var tx = await ctx.Database.BeginTransactionAsync();
			var amount = await ctx.Mails.Where(m => m.MailId == mailId).Select(m => m.Money).FirstOrDefaultAsync();
			if (amount == 0)
				return 0;

			await ctx.Mails.Where(m => m.MailId == mailId).ExecuteUpdateAsync(s => s.SetProperty(m => m.Money, 0UL));
			await ctx.Characters.Where(c => c.Id == characterId).ExecuteUpdateAsync(s => s.SetProperty(c => c.Money, wallet + amount));
			await tx.CommitAsync();
			return amount;
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

		// The next wire MailId: max + 1 (MailId is the global PK), a deterministic sequence rather than a random id with
		// collision retry. Computed in memory because SQLite cannot MAX a ulong (its integer is signed 64-bit).
		private static async Task<ulong> NextMailId(DatabaseContext ctx)
		{
			var mailIds = await ctx.Mails.Select(m => m.MailId).ToListAsync();
			return mailIds.Count == 0
				? 1UL
				: mailIds.Max() + 1UL;
		}
	}
}
