using System;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Repositories
{
	public interface IAccountRepository
	{
		Task CreateAccountClaim(AccountClaim claim);
		Task DeleteAccountClaim(uint claimId);
		Task<Account?> GetAccount(uint id);
		Task<Account?> GetAccount(string userName);
		Task<AccountClaim?> GetAccountClaim(uint claimId);
		Task<AccountClaim[]> GetAccountClaims(uint accountId);
		Task SaveAccount(Account account);
		Task SetAccountClaimQuantity(uint claimId, uint? quantity);
		Task SetAccountClaimsAvailable(uint accountId, bool available);
		Task<bool> SetAccountAuthChallenge(uint accountId, Guid challenge);
		Task<bool> SetAccountLastInfo(uint accountId, string ipAddress);
		Task<bool> SetAccountState(uint accountId, AccountState state);
		Task SetAccountSubscriptionSettings(uint accountId, byte[] settings);
	}

	public partial class DatabaseRepository
	{
		public async Task CreateAccountClaim(AccountClaim claim)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			ctx.AccountClaims.Add(claim);
			await ctx.SaveChangesAsync();
			await ctx.Accounts.Where(a => a.Id == claim.AccountId).ExecuteUpdateAsync(s => s.SetProperty(a => a.ClaimsAvailable, true));
		}

		public async Task DeleteAccountClaim(uint claimId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx.AccountClaims.Where(c => c.Id == claimId).ExecuteDeleteAsync();
		}

		public async Task<Account?> GetAccount(uint id)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Accounts.FirstOrDefaultAsync(a => a.Id == id);
		}

		// Case-INSENSITIVE, matching how the web login has always looked accounts up (Identity's FindByNameAsync
		// normalises to upper-case). This was an exact match, so the two logins disagreed: the web let you in as
		// "Faolan" when the row said "faolan" and the game did not, for the same account and the same password.
		// Usernames are alphanumeric ASCII, so UPPER() folding is exact here.
		//
		// ⚠️ The unique index on UserName is still BINARY collation, so the DATABASE would accept "Faolan" and
		// "faolan" as two rows even though nothing can now tell them apart. Nothing creates such a pair today -
		// registration has always checked case-insensitively - but making the index NOCASE is the real fix.
		public async Task<Account?> GetAccount(string userName)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Accounts.FirstOrDefaultAsync(a => a.UserName.ToUpper() == userName.ToUpper());
		}

		public async Task<AccountClaim?> GetAccountClaim(uint claimId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.AccountClaims.Include(c => c.Item).FirstOrDefaultAsync(c => c.Id == claimId);
		}

		public async Task<AccountClaim[]> GetAccountClaims(uint accountId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.AccountClaims.Include(c => c.Item).Where(c => c.AccountId == accountId).OrderBy(c => c.Id).ToArrayAsync();
		}

		public async Task SaveAccount(Account account)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx
				.Accounts.Where(a => a.Id == account.Id)
				.ExecuteUpdateAsync(s => s
					.SetProperty(a => a.AuthStatus, account.AuthStatus)
					.SetProperty(a => a.ClientInstance, account.ClientInstance)
					.SetProperty(a => a.GmLevel, account.GmLevel));
		}

		// The account-wide client settings archive (#611). Its own method rather than a field on SaveAccount: that one
		// runs on nearly every login step, and rewriting a 1 KB blob each time would be pure churn.
		public async Task SetAccountSubscriptionSettings(uint accountId, byte[] settings)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx.Accounts.Where(a => a.Id == accountId).ExecuteUpdateAsync(s => s.SetProperty(a => a.SubscriptionSettings, settings));
		}

		public async Task SetAccountClaimQuantity(uint claimId, uint? quantity)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx.AccountClaims.Where(c => c.Id == claimId).ExecuteUpdateAsync(s => s.SetProperty(c => c.Quantity, quantity));
		}

		public async Task SetAccountClaimsAvailable(uint accountId, bool available)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx.Accounts.Where(a => a.Id == accountId).ExecuteUpdateAsync(s => s.SetProperty(a => a.ClaimsAvailable, available));
		}

		// Rotates the login nonce. Called on every Authenticate BEFORE the challenge goes out, so a login block
		// captured off the wire can never be replayed against the next attempt.
		public async Task<bool> SetAccountAuthChallenge(uint accountId, Guid challenge)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Accounts.Where(a => a.Id == accountId).ExecuteUpdateAsync(s => s.SetProperty(a => a.AuthChallenge, challenge)) == 1;
		}

		public async Task<bool> SetAccountLastInfo(uint accountId, string ipAddress)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Accounts.Where(a => a.Id == accountId).ExecuteUpdateAsync(s => s.SetProperty(a => a.LastConnection, DateTimeOffset.UtcNow).SetProperty(a => a.LastIpAddress, ipAddress)) ==
			       1;
		}

		public async Task<bool> SetAccountState(uint accountId, AccountState state)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Accounts.Where(a => a.Id == accountId).ExecuteUpdateAsync(s => s.SetProperty(a => a.State, state)) == 1;
		}
	}
}
