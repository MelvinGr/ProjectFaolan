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
	// A mail attachment ESCROWS the item row, it does not describe one.
	//
	// Before `MailAttachment.EscrowedInstanceId` the attachment carried a template id, the take minted a fresh item
	// from it and the seller's row was deleted outright - so an auction-house sale delivered a **gemless,
	// crafterless copy**. That is the same bug #750 fixed for player trades (`a3fb97e8`), which had been left live
	// in the mail and AH paths because they were not looked at.
	//
	// These gates are on the MODEL rather than the service, because that is where the bug lived: what the attachment
	// can point at, and whether the row survives the round trip. `TradepostServiceTests` covers the wire side.
	public class MailEscrowTests
	{
		private const uint Buyer = 7002;
		private const uint GemTemplate = 3983088;
		private const uint Seller = 7001;
		private const uint SwordTemplate = 157123;

		// The whole point: a crafted, gemmed item put in escrow and handed on is the SAME ROW, so its crafter and
		// its gems are still there. Minting from a template loses both, silently.
		[Fact]
		public async Task AnEscrowedItem_KeepsItsCrafterAndGemsAcrossTheHandOver()
		{
			var (options, path) = TestDb.NewSeededDatabase(Seller);
			try
			{
				uint instanceId;
				ulong mailId;
				await using (var ctx = new DatabaseContext(options))
				{
					var realm = await ctx.Realms.Select(r => r.Id).FirstAsync(TestContext.Current.CancellationToken);
					var account = await ctx.Accounts.Select(a => a.Id).FirstAsync(TestContext.Current.CancellationToken);
					ctx.Characters.Add(new Character
					{
						Id = Buyer, Name = "Buyer", Class = ConanClass.Barbarian, Level = 20, Race = Race.Cimmerian,
						Sex = Sex.Male, RealmId = realm, AccountId = account
					});
					ctx.Items.Add(new Item { Id = SwordTemplate, Name = "Bloodsilver Khopesh", Key = new ProtoDecryptionKey() });
					ctx.Items.Add(new Item { Id = GemTemplate, Name = "Sanguine Radiant Sapphire", Key = new ProtoDecryptionKey() });

					// The seller's listed item: player-crafted, and gemmed. Both facts live ONLY on the row.
					var listed = new ItemInstance
					{
						OwnerCharacterId = Seller, Container = ContainerKind.Bank, Slot = 6, TemplateId = SwordTemplate, Count = 1, CreatorName = "NecroL80",
						Sockets = { new ItemInstanceSocket { SocketIndex = 0, GemItemId = GemTemplate } }
					};
					ctx.ItemInstances.Add(listed);
					await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
					instanceId = listed.Id;

					mailId = 0x20D82B6300000001;
					ctx.Mails.Add(new Mail
					{
						MailId = mailId, RealmId = realm, RecipientName = "Buyer", SenderName = "", BodyXml = "",
						Attachments = { new MailAttachment { ItemInstanceId = mailId, Template = SwordTemplate, Quantity = 1, EscrowedInstanceId = instanceId } }
					});
					await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
				}

				var database = new DatabaseRepository(new TestDb.ContextFactory(options));

				// The escrow, as the take path reads it - the row, its template and its sockets all loaded.
				var mail = await database.GetMailByWireId(mailId);
				var escrowed = Assert.Single(mail!.Attachments).Escrowed;
				Assert.NotNull(escrowed);
				Assert.Equal("NecroL80", escrowed!.CreatorName);
				Assert.Equal(GemTemplate, Assert.Single(escrowed.Sockets).GemItemId);

				// The hand-over: the SAME row moves to the buyer.
				var delivered = await database.TransferCharacterItem(escrowed.Id, Buyer, ContainerKind.Bag, 3);

				Assert.NotNull(delivered);
				Assert.Equal(instanceId, delivered!.Id); // the same row, not a copy
				Assert.Equal(Buyer, delivered.OwnerCharacterId);
				Assert.Equal("NecroL80", delivered.CreatorName);
				Assert.Equal(GemTemplate, Assert.Single(delivered.Sockets).GemItemId);

				// ...and it is not still sitting in the seller's bank.
				Assert.Null(await database.GetCharacterItemSlot(Seller, ContainerKind.Bank, 6));
			}
			finally
			{
				TestDb.Delete(path);
			}
		}

		// The importer-seeded half. Those listings have no seller character and never had an item row, so their
		// purchase mail carries a NULL escrow and the take still mints from the template - which is why the column
		// is nullable rather than required.
		[Fact]
		public async Task AnAttachmentWithNoEscrow_IsStillValid()
		{
			var (options, path) = TestDb.NewSeededDatabase(Seller);
			try
			{
				const ulong mailId = 0x20D82B6300000002;
				await using (var ctx = new DatabaseContext(options))
				{
					var realm = await ctx.Realms.Select(r => r.Id).FirstAsync(TestContext.Current.CancellationToken);
					ctx.Items.Add(new Item { Id = SwordTemplate, Name = "Bloodsilver Khopesh", Key = new ProtoDecryptionKey() });
					ctx.Mails.Add(new Mail
					{
						MailId = mailId, RealmId = realm, RecipientName = "Seller", SenderName = "", BodyXml = "",
						Attachments = { new MailAttachment { ItemInstanceId = mailId, Template = SwordTemplate, Quantity = 1 } }
					});
					await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
				}

				var database = new DatabaseRepository(new TestDb.ContextFactory(options));
				var mail = await database.GetMailByWireId(mailId);

				var attachment = Assert.Single(mail!.Attachments);
				Assert.Null(attachment.Escrowed);
				Assert.Equal(SwordTemplate, attachment.Template); // the template is the only source, and still there
			}
			finally
			{
				TestDb.Delete(path);
			}
		}
	}
}
