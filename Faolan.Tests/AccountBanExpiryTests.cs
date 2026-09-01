using System;
using System.IO;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// #918: a TIMED ban (`.kickban`) used to schedule its own lift with an in-process `Task.Delay`, so a restart
	// inside the window left the account banned until someone ran `.unban` by hand - and a restart that spanned the
	// whole window could not release it either. The lift is now a property of the DATA: `Account.BanExpiresAt` is
	// written with the state, and the login gate lifts it when the moment has passed.
	public class AccountBanExpiryTests
	{
		[Fact]
		public async Task ATimedBan_LiftsItselfOnceItsMomentHasPassed()
		{
			await WithAccount(async (repo, accountId) =>
			{
				Assert.True(await repo.BanUntil(accountId, DateTimeOffset.UtcNow.AddMinutes(-1)));

				// Nothing has read it yet, so the account is still banned - the state remains the single source of
				// truth and the expiry only says when it should stop applying.
				Assert.Equal(AccountState.Banned, (await repo.GetAccount(accountId))!.State);

				Assert.True(await repo.ClearExpiredBan(accountId, DateTimeOffset.UtcNow));

				var after = (await repo.GetAccount(accountId))!;
				Assert.Equal(AccountState.Active, after.State);
				Assert.Null(after.BanExpiresAt);
			});
		}

		[Fact]
		// The other half of "survives a restart in both directions": a server that comes back INSIDE the window must
		// still hold the ban.
		public async Task ATimedBan_IsNotLiftedBeforeItsMoment()
		{
			await WithAccount(async (repo, accountId) =>
			{
				await repo.BanUntil(accountId, DateTimeOffset.UtcNow.AddMinutes(30));

				Assert.False(await repo.ClearExpiredBan(accountId, DateTimeOffset.UtcNow));
				Assert.Equal(AccountState.Banned, (await repo.GetAccount(accountId))!.State);
			});
		}

		[Fact]
		// ⚠️ An INDEFINITE ban (`.ban`, no expiry) must never be caught by the sweep, however long it has stood.
		public async Task AnIndefiniteBan_IsNeverLifted()
		{
			await WithAccount(async (repo, accountId) =>
			{
				await repo.SetAccountState(accountId, AccountState.Banned);

				Assert.False(await repo.ClearExpiredBan(accountId, DateTimeOffset.UtcNow.AddYears(10)));
				Assert.Equal(AccountState.Banned, (await repo.GetAccount(accountId))!.State);
			});
		}

		[Fact]
		// ⚠️ A state change SUPERSEDES a pending lift. Without this an indefinite `.ban` laid over an unexpired
		// `.kickban` would inherit its expiry and silently release itself - the state would say "banned for ever" and
		// the data would disagree.
		public async Task AnIndefiniteBanOverATimedOne_DoesNotInheritTheExpiry()
		{
			await WithAccount(async (repo, accountId) =>
			{
				await repo.BanUntil(accountId, DateTimeOffset.UtcNow.AddMinutes(5));
				await repo.SetAccountState(accountId, AccountState.Banned);

				Assert.Null((await repo.GetAccount(accountId))!.BanExpiresAt);
				Assert.False(await repo.ClearExpiredBan(accountId, DateTimeOffset.UtcNow.AddYears(10)));
			});
		}

		[Fact]
		// ...and `.unban` clears the expiry too, so a later re-ban starts clean.
		public async Task UnbanClearsTheExpiry()
		{
			await WithAccount(async (repo, accountId) =>
			{
				await repo.BanUntil(accountId, DateTimeOffset.UtcNow.AddMinutes(5));
				await repo.SetAccountState(accountId, AccountState.Active);

				var after = (await repo.GetAccount(accountId))!;
				Assert.Equal(AccountState.Active, after.State);
				Assert.Null(after.BanExpiresAt);
			});
		}

		[Fact]
		// An account that is not banned at all is untouched, whatever the sweep is asked.
		public async Task AnActiveAccountIsUntouched()
		{
			await WithAccount(async (repo, accountId) =>
			{
				Assert.False(await repo.ClearExpiredBan(accountId, DateTimeOffset.UtcNow.AddYears(10)));
				Assert.Equal(AccountState.Active, (await repo.GetAccount(accountId))!.State);
			});
		}

		private sealed class BanTestContextFactory : IDbContextFactory<DatabaseContext>
		{
			private readonly DbContextOptions<DatabaseContext> _options;

			public BanTestContextFactory(DbContextOptions<DatabaseContext> options)
			{
				_options = options;
			}

			public DatabaseContext CreateDbContext()
			{
				return new DatabaseContext(_options);
			}
		}

		private static async Task WithAccount(Func<DatabaseRepository, uint, Task> body)
		{
			var dbPath = Path.Combine(Path.GetTempPath(), $"faolan-ban-{Guid.NewGuid():N}.db");
			try
			{
				var options = TestDb.Options(dbPath);
				IDbContextFactory<DatabaseContext> factory = new BanTestContextFactory(options);
				await using (var ctx = await factory.CreateDbContextAsync(TestContext.Current.CancellationToken))
				{
					await ctx.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
					ctx.Accounts.Add(new Account
					{
						Id = 1,
						UserName = "bansubject",
						Password = "x",
						State = AccountState.Active
					});
					await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
				}

				await body(new DatabaseRepository(factory), 1);
			}
			finally
			{
				if (File.Exists(dbPath))
					File.Delete(dbPath);
			}
		}
	}
}
