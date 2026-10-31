using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Xunit;

namespace Faolan.Tests
{
	// Every tier after the UniverseAgent is a new connection on a port of its own, and the client tells it who it is.
	// What makes that claim worth anything is the login cookie the UniverseAgent issued: an account or character id
	// alone is on the wire in every login, so a handshake that checked only the id would let anyone skip the password.
	public class LoginCookieTests
	{
		private const uint Account = 1;
		private const uint Character = 0x01000001;
		private const uint OtherAccount = 2;

		[Fact]
		public async Task AnAccount_IsReachedOnlyWithItsCurrentCookie()
		{
			var (options, path) = TestDb.NewSeededDatabase(Character);
			try
			{
				var repo = new DatabaseRepository(new TestDb.ContextFactory(options));

				// Never logged in: no cookie at all, so nothing matches - not even zero.
				Assert.Null(await repo.GetAccountForLogin(Account, 0));

				await repo.SetAccountLoginCookie(Account, 0x1234);
				Assert.Equal(Account, (await repo.GetAccountForLogin(Account, 0x1234))?.Id);
				Assert.Null(await repo.GetAccountForLogin(Account, 0x1235));

				// The next login replaces it; the previous one stops opening anything.
				await repo.SetAccountLoginCookie(Account, 0x5678);
				Assert.Null(await repo.GetAccountForLogin(Account, 0x1234));
				Assert.NotNull(await repo.GetAccountForLogin(Account, 0x5678));
			}
			finally
			{
				TestDb.Delete(path);
			}
		}

		[Fact]
		public async Task ACharacter_IsReachedOnlyWithItsOwnAccountsCookie()
		{
			var (options, path) = TestDb.NewSeededDatabase(Character);
			try
			{
				await using (var ctx = new DatabaseContext(options))
				{
					ctx.Accounts.Add(new Account
					{
						Id = OtherAccount,
						UserName = "other",
						Password = "t",
						LastIpAddress = ""
					});
					await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
				}

				var repo = new DatabaseRepository(new TestDb.ContextFactory(options));
				await repo.SetAccountLoginCookie(Account, 0x1111);
				await repo.SetAccountLoginCookie(OtherAccount, 0x2222);

				var loggedIn = await repo.GetCharacterForLogin(Character, 0x1111);
				Assert.NotNull(loggedIn);
				Assert.Equal((Character, Account), (loggedIn.Id, loggedIn.Account.Id));

				// Another account's perfectly valid cookie does not open this account's character.
				Assert.Null(await repo.GetCharacterForLogin(Character, 0x2222));
				Assert.Null(await repo.GetCharacterForLogin(Character, 0));
			}
			finally
			{
				TestDb.Delete(path);
			}
		}
	}
}
