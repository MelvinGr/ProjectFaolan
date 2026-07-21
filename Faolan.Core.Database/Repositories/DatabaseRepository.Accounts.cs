using System;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Repositories
{
	public partial interface IDatabaseRepository
	{
		Task<bool> CheckLogin(string username, string password);
		Task CreateAccountClaim(AccountClaim claim);
		Task DeleteAccountClaim(uint claimId);
		Task<Account?> GetAccount(uint id);
		Task<Account?> GetAccount(string userName);
		Task<AccountClaim?> GetAccountClaim(uint claimId);
		Task<AccountClaim[]> GetAccountClaims(uint accountId);
		Task SaveAccount(Account account);
		Task SetAccountClaimQuantity(uint claimId, uint? quantity);
		Task SetAccountClaimsAvailable(uint accountId, bool available);
		Task<bool> SetAccountLastInfo(uint accountId, string ipAddress);
		Task<bool> SetAccountPassword(uint accountId, string password);
		Task<bool> SetAccountState(uint accountId, AccountState state);
	}

	public partial class DatabaseRepository
	{
		public async Task<bool> CheckLogin(string username, string password)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Accounts.AnyAsync(a => a.UserName == username && a.Password == password);
		}

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

		public async Task<Account?> GetAccount(string userName)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Accounts.FirstOrDefaultAsync(a => a.UserName == userName);
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
					.SetProperty(a => a.GmGodMode, account.GmGodMode)
					.SetProperty(a => a.GmInvisible, account.GmInvisible));
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

		public async Task<bool> SetAccountLastInfo(uint accountId, string ipAddress)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Accounts.Where(a => a.Id == accountId).ExecuteUpdateAsync(s => s.SetProperty(a => a.LastConnection, DateTimeOffset.UtcNow).SetProperty(a => a.LastIpAddress, ipAddress)) ==
			       1;
		}

		public async Task<bool> SetAccountPassword(uint accountId, string password)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Accounts.Where(a => a.Id == accountId).ExecuteUpdateAsync(s => s.SetProperty(a => a.Password, password)) == 1;
		}

		public async Task<bool> SetAccountState(uint accountId, AccountState state)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Accounts.Where(a => a.Id == accountId).ExecuteUpdateAsync(s => s.SetProperty(a => a.State, state)) == 1;
		}
	}
}
