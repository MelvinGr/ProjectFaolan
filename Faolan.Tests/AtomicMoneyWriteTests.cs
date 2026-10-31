using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// Money moves in single statements or single transactions, never as a loaded row written back whole. The guild
	// bank used to be read, changed in memory and saved as a whole row, so two deposits at once kept one; a mail's
	// money was credited and then cleared in two writes, so a failure between them left it to be banked again.
	public class AtomicMoneyWriteTests
	{
		private const uint Character = 0x01000001;

		[Fact]
		public async Task GuildCash_DepositsAndWithdrawsInPlace_AndRefusesAnOverdraft()
		{
			var (options, path) = TestDb.NewSeededDatabase(Character);
			try
			{
				uint guildId;
				await using (var ctx = new DatabaseContext(options))
				{
					var guild = new Guild { Name = "Bank", RealmId = (await ctx.Realms.FirstAsync(TestContext.Current.CancellationToken)).Id };
					ctx.Guilds.Add(guild);
					await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
					guildId = guild.Id;
				}

				var repo = new DatabaseRepository(new TestDb.ContextFactory(options));

				Assert.Equal(500ul, await repo.DepositGuildCash(guildId, 500));
				Assert.Equal(700ul, await repo.DepositGuildCash(guildId, 200));
				Assert.Null(await repo.WithdrawGuildCash(guildId, 701));
				Assert.Equal(1ul, await repo.WithdrawGuildCash(guildId, 699));
				Assert.Null(await repo.DepositGuildCash(guildId + 1, 5));

				await repo.SetGuildMessageOfTheDay(guildId, "hold the gate");
				var stored = await repo.GetGuild(guildId);
				Assert.Equal((1ul, "hold the gate"), (stored!.Cash, stored.MessageOfTheDay));
			}
			finally
			{
				TestDb.Delete(path);
			}
		}

		[Fact]
		public async Task MailMoney_MovesOnce_IntoTheWallet()
		{
			var (options, path) = TestDb.NewSeededDatabase(Character);
			try
			{
				var repo = new DatabaseRepository(new TestDb.ContextFactory(options));
				uint realm;
				await using (var ctx = new DatabaseContext(options))
					realm = (await ctx.Realms.FirstAsync(TestContext.Current.CancellationToken)).Id;

				var mail = await repo.CreateMail(id => new Mail { MailId = id, RealmId = realm, RecipientName = "Testchar", SenderName = "Sender", BodyXml = "", Money = 250 });

				Assert.Equal(250ul, await repo.ClaimMailMoney(mail.MailId, Character, 1000));
				Assert.Equal(0ul, await repo.ClaimMailMoney(mail.MailId, Character, 1250));

				await using (var ctx = new DatabaseContext(options))
				{
					Assert.Equal(1250ul, (await ctx.Characters.SingleAsync(c => c.Id == Character, TestContext.Current.CancellationToken)).Money);
					Assert.Equal(0ul, (await ctx.Mails.SingleAsync(TestContext.Current.CancellationToken)).Money);
				}
			}
			finally
			{
				TestDb.Delete(path);
			}
		}
	}
}
