using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Protobuf;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// An auction-house purchase is one unit of work (`CompleteTradepostPurchase`): the listing is claimed, the buyer
	// charged, the seller's row escrowed and the mail written together.
	//
	// The escrow used a fixed Mail cell, so a buyer holding one untaken purchase had the next one's move refused, and
	// the refusal fell through to deleting the seller's row and minting a bare copy at the take. Nothing claimed the
	// listing either, so two buyers could both pay for it.
	public class TradepostPurchaseTests
	{
		private const uint Buyer = 7002;
		private const uint OtherBuyer = 7003;
		private const uint Seller = 7001;
		private const uint SwordTemplate = 157123;

		[Fact]
		public async Task TwoUntakenPurchases_EscrowBothRows_IntoSeparateCells()
		{
			var (options, path) = TestDb.NewSeededDatabase(Seller);
			try
			{
				var (realm, first, second) = await Seed(options);
				var database = new DatabaseRepository(new TestDb.ContextFactory(options));

				var mailA = await database.CompleteTradepostPurchase(Listing(realm, 1, 6), Buyer, 900, Seller, id => PurchaseMail(id, realm, 1));
				var mailB = await database.CompleteTradepostPurchase(Listing(realm, 2, 7), Buyer, 800, Seller, id => PurchaseMail(id, realm, 2));

				Assert.Equal(first, Assert.Single(mailA!.Attachments).EscrowedInstanceId);
				Assert.Equal(second, Assert.Single(mailB!.Attachments).EscrowedInstanceId);
				Assert.NotEqual(mailA.MailId, mailB.MailId);

				await using var ctx = new DatabaseContext(options);
				var rows = await ctx.ItemInstances.Where(i => i.Id == first || i.Id == second).OrderBy(i => i.Id).ToListAsync(TestContext.Current.CancellationToken);
				Assert.All(rows, r => Assert.Equal((Buyer, ContainerKind.Mail), (r.OwnerCharacterId!.Value, r.Container)));
				Assert.Equal([0, 1], rows.Select(r => r.Slot));
				Assert.Equal(800ul, (await ctx.Characters.SingleAsync(c => c.Id == Buyer, TestContext.Current.CancellationToken)).Money);
				Assert.Empty(await ctx.TradepostListings.ToListAsync(TestContext.Current.CancellationToken));
			}
			finally
			{
				TestDb.Delete(path);
			}
		}

		[Fact]
		public async Task ASecondBuyer_FindsTheListingGone_AndPaysNothing()
		{
			var (options, path) = TestDb.NewSeededDatabase(Seller);
			try
			{
				var (realm, first, _) = await Seed(options);
				var database = new DatabaseRepository(new TestDb.ContextFactory(options));

				Assert.NotNull(await database.CompleteTradepostPurchase(Listing(realm, 1, 6), Buyer, 900, Seller, id => PurchaseMail(id, realm, 1)));
				Assert.Null(await database.CompleteTradepostPurchase(Listing(realm, 1, 6), OtherBuyer, 0, Seller, id => PurchaseMail(id, realm, 1)));

				await using var ctx = new DatabaseContext(options);
				Assert.Equal(1000ul, (await ctx.Characters.SingleAsync(c => c.Id == OtherBuyer, TestContext.Current.CancellationToken)).Money);
				Assert.Single(await ctx.Mails.ToListAsync(TestContext.Current.CancellationToken));
				Assert.Equal(Buyer, (await ctx.ItemInstances.SingleAsync(i => i.Id == first, TestContext.Current.CancellationToken)).OwnerCharacterId);
			}
			finally
			{
				TestDb.Delete(path);
			}
		}

		private static TradepostListing Listing(uint realm, ulong saleId, uint bankSlot)
		{
			return new TradepostListing
			{
				RealmId = realm,
				SaleId = saleId,
				ItemId = SwordTemplate,
				Template = SwordTemplate,
				Price = 100,
				SellerName = "Testchar",
				BankSlot = bankSlot
			};
		}

		private static Mail PurchaseMail(ulong mailId, uint realm, ulong saleId)
		{
			return new Mail
			{
				MailId = mailId, RealmId = realm, RecipientName = "Buyer", SenderName = "", BodyXml = "",
				Attachments = { new MailAttachment { ItemInstanceId = saleId, Template = SwordTemplate, Quantity = 1 } }
			};
		}

		// The seller (the seeded character) with two crafted swords listed out of bank slots 6 and 7, and two buyers.
		private static async Task<(uint Realm, uint First, uint Second)> Seed(DbContextOptions<DatabaseContext> options)
		{
			await using var ctx = new DatabaseContext(options);
			var realm = await ctx.Realms.Select(r => r.Id).FirstAsync(TestContext.Current.CancellationToken);
			var account = await ctx.Accounts.Select(a => a.Id).FirstAsync(TestContext.Current.CancellationToken);
			foreach (var (id, name) in new[] { (Buyer, "Buyer"), (OtherBuyer, "Otherbuyer") })
			{
				ctx.Characters.Add(new Character
				{
					Id = id, Name = name, Class = ConanClass.Barbarian, Level = 20, Race = Race.Cimmerian, Sex = Sex.Male, RealmId = realm, AccountId = account,
					Money = 1000
				});
			}

			ctx.Items.Add(new Item { Id = SwordTemplate, Name = "Bloodsilver Khopesh", Key = new ProtoDecryptionKey() });
			var first = new ItemInstance { OwnerCharacterId = Seller, Container = ContainerKind.Bank, Slot = 6, TemplateId = SwordTemplate, Count = 1, CreatorName = "NecroL80" };
			var second = new ItemInstance { OwnerCharacterId = Seller, Container = ContainerKind.Bank, Slot = 7, TemplateId = SwordTemplate, Count = 1, CreatorName = "NecroL80" };
			ctx.ItemInstances.AddRange(first, second);
			ctx.TradepostListings.AddRange(Listing(realm, 1, 6), Listing(realm, 2, 7));
			await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
			return (realm, first.Id, second.Id);
		}
	}
}
