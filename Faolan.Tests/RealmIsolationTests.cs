using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Xunit;

namespace Faolan.Tests
{
	// The isolation half of multi-realm: guilds, auction-house listings, mail and character names are realm-scoped, and the
	// thing that enforces that is the query path, not the schema.
	//
	// That distinction is the whole reason this class exists. The three-process deployment run on 2026-08-31
	// established the model half - exactly four tables carry `RealmId`, and `IX_Characters_RealmId_Name` really does
	// let one name exist once per realm - but a column nothing filters on isolates nothing, and a missed `WHERE`
	// fails quietly: realm B's guild list simply shows up in realm A, with no error anywhere. So every assertion
	// below is written the way the leak would appear - two realms holding rows that collide on everything except
	// their realm (same guild name, same sale id, same seller and slot, same recipient, same character name) - and
	// then asks each realm's own reader for its own.
	//
	// A test can reach this where a client cannot: proving it in-client needs two realm processes and two accounts
	// playing simultaneously, while the property being proven is one `WHERE` clause per read.
	public class RealmIsolationTests : IDisposable
	{
		private const uint RealmA = 1;
		private const uint RealmB = 2;
		private const string SharedName = "Hyborians"; // the collider: used as a guild name, a seller and a recipient
		private const uint Template = 0x7530;

		private readonly string _path = Path.Combine(Path.GetTempPath(), $"faolan-realms-{Guid.NewGuid():N}.db");

		public void Dispose()
		{
			TestDb.Delete(_path);
			GC.SuppressFinalize(this);
		}

		[Fact]
		public async Task GuildReads_SeeOnlyTheirOwnRealm()
		{
			var repo = await Seed();

			var a = await repo.GetGuildByName(SharedName, RealmA);
			var b = await repo.GetGuildByName(SharedName, RealmB);

			Assert.NotNull(a);
			Assert.NotNull(b);
			Assert.NotEqual(a.Id, b.Id); // the same name is two different guilds
			Assert.Equal(RealmA, a.RealmId);
			Assert.Equal(RealmB, b.RealmId);

			Assert.Equal([RealmA], (await repo.GetGuilds(RealmA)).Select(g => g.RealmId).Distinct());
			Assert.Single(await repo.GetGuilds(RealmA));
		}

		// The auction house is the widest surface - six readers and two writers all take a realmId - so it is checked
		// per method rather than through the list reader alone. Both realms hold sale id 1 for the same item, sold by
		// the same seller out of the same bank slot: every one of these would answer with the wrong realm's row if
		// its filter were missing.
		[Fact]
		public async Task TradepostReads_SeeOnlyTheirOwnRealm()
		{
			var repo = await Seed();

			Assert.Equal(100ul, (await repo.GetTradepostListingBySaleId(1, RealmA))!.Price);
			Assert.Equal(200ul, (await repo.GetTradepostListingBySaleId(1, RealmB))!.Price);

			Assert.Equal(100ul, (await repo.GetTradepostListingBySellerSlot(SharedName, 0, RealmA))!.Price);
			Assert.Equal(200ul, (await repo.GetTradepostListingBySellerSlot(SharedName, 0, RealmB))!.Price);

			Assert.Equal([100ul], (await repo.GetTradepostListings(Template, RealmA)).Select(l => l.Price));
			Assert.Equal([200ul], (await repo.GetTradepostListings(Template, RealmB)).Select(l => l.Price));

			// Sale ids are allocated per realm (`IX_TradepostListings_RealmId_SaleId`), so a realm's next id must not
			// be pushed along by another realm's listings.
			Assert.Equal(2ul, await repo.GetNextTradepostSaleId(RealmA));
			Assert.Equal(2ul, await repo.GetNextTradepostSaleId(RealmB));
		}

		// The two writers, which is where a missing filter does damage rather than merely showing the wrong number:
		// a cancel or a re-price keyed on the sale id alone would reach into the other realm's auction house.
		[Fact]
		public async Task TradepostWrites_TouchOnlyTheirOwnRealm()
		{
			var repo = await Seed();

			await repo.SetTradepostListingPrice(1, RealmA, 999);
			Assert.Equal(999ul, (await repo.GetTradepostListingBySaleId(1, RealmA))!.Price);
			Assert.Equal(200ul, (await repo.GetTradepostListingBySaleId(1, RealmB))!.Price);

			await repo.DeleteTradepostListing(1, RealmA);
			Assert.Null(await repo.GetTradepostListingBySaleId(1, RealmA));
			Assert.NotNull(await repo.GetTradepostListingBySaleId(1, RealmB)); // the other realm's sale id 1 survives
		}

		// Mail is addressed by recipient name, and a name is only unique within a realm - so this is the one table
		// where the realm filter is what stops mail being delivered to a stranger who happens to share the name.
		[Fact]
		public async Task MailReads_SeeOnlyTheirOwnRealm()
		{
			var repo = await Seed();

			Assert.Equal([RealmA], (await repo.GetMailsForRecipient(SharedName, RealmA)).Select(m => m.RealmId));
			Assert.Equal([RealmB], (await repo.GetMailsForRecipient(SharedName, RealmB)).Select(m => m.RealmId));
		}

		// The name rule from both ends: the same name is two characters in two realms, and `GetCharacterByName`
		// hands back the one belonging to the realm it was asked about.
		[Fact]
		public async Task CharacterNames_AreUniquePerRealmAndResolvePerRealm()
		{
			var repo = await Seed();

			var a = await repo.GetCharacterByName(SharedName, RealmA);
			var b = await repo.GetCharacterByName(SharedName, RealmB);

			Assert.NotNull(a);
			Assert.NotNull(b);
			Assert.NotEqual(a.Id, b.Id);
			Assert.Equal(RealmA, a.RealmId);
			Assert.Equal(RealmB, b.RealmId);
		}

		// The other half of the name rule, and the reason the index is composite rather than two indexes: one realm
		// still may not hold the name twice.
		[Fact]
		public async Task ACharacterName_CannotBeTakenTwiceInOneRealm()
		{
			await Seed();

			await using var ctx = new DatabaseContext(TestDb.Options(_path));
			ctx.Characters.Add(NewCharacter(0x0300_0001, RealmA, SharedName));

			await Assert.ThrowsAnyAsync<Exception>(() => ctx.SaveChangesAsync(TestContext.Current.CancellationToken));
		}

		// The character list is deliberately not realm-scoped, and that is not an oversight to be "fixed": the
		// login tier owns character selection, an account's characters live across realms, and the wire row carries
		// `character.RealmId` per entry (PlayerAgentListener.SendCharacterList) precisely so the client can show
		// which realm each one is on. Filtering it by realm would hide the account's other characters at selection.
		[Fact]
		public async Task TheAccountsCharacterList_SpansRealmsByDesign()
		{
			var repo = await Seed();

			var characters = await repo.GetCharactersByAccount(1);

			Assert.Equal([RealmA, RealmB], characters.Select(c => c.RealmId).Order());
		}

		private static Character NewCharacter(uint id, uint realmId, string name)
		{
			return new Character
			{
				Id = id,
				AccountId = 1,
				RealmId = realmId,
				MapId = Map.TortageBeachId,
				Name = name,
				Class = ConanClass.Barbarian,
				Race = Race.Cimmerian,
				Sex = Sex.Male
			};
		}

		// Two realms holding rows that collide on everything but the realm. Prices 100 (A) and 200 (B) are the tell:
		// every assertion above reads a price back, so a leak shows as the wrong number rather than as a count.
		private async Task<DatabaseRepository> Seed()
		{
			var options = TestDb.Options(_path);
			await using (var ctx = new DatabaseContext(options))
			{
				await ctx.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

				TestDb.SeedWorld(ctx, realmId: RealmA);
				ctx.Realms.Add(new Realm
				{
					Id = RealmB,
					Name = "Test B",
					IpAddress = "127.0.0.1"
				});
				ctx.Items.Add(new Item
				{
					Id = Template,
					Name = "Ware"
				});
				await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

				foreach (var (realmId, price, characterId, mailId) in new[]
					{
						(RealmA, 100ul, 0x0100_0001u, 1ul),
						(RealmB, 200ul, 0x0200_0001u, 2ul)
					})
				{
					ctx.Characters.Add(NewCharacter(characterId, realmId, SharedName));
					ctx.Guilds.Add(new Guild
					{
						RealmId = realmId,
						Name = SharedName
					});
					ctx.TradepostListings.Add(new TradepostListing
					{
						SaleId = 1, // the same sale id in both realms - legal, and the collider the readers must survive
						RealmId = realmId,
						ItemId = Template,
						Template = Template,
						Price = price,
						SellerName = SharedName,
						BankSlot = 0
					});
					ctx.Mails.Add(new Mail
					{
						MailId = mailId, // MailId is the global pk - mail is scoped by the RealmId column alone
						RealmId = realmId,
						RecipientName = SharedName,
						SenderName = "Sender",
						BodyXml = "<mail/>"
					});
				}

				await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
			}

			return new DatabaseRepository(new TestDb.ContextFactory(options));
		}
	}
}
