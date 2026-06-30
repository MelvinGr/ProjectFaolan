using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Web.Services
{
	// One account row for the admin table.
	public sealed record AccountRow(
		uint Id, string UserName, AccountType Type, AccountState State, DateTime? LastConnection, int Characters);

	// Admin actions over the shared game Accounts table (ban/unban, account type). Writes go to the same faolan.db the
	// game server reads, so changes take effect on the player's next login (the server enforces State/Type at auth).
	public sealed class AccountAdminService
	{
		private readonly IDbContextFactory<DatabaseContext> _dbFactory;

		public AccountAdminService(IDbContextFactory<DatabaseContext> dbFactory) => _dbFactory = dbFactory;

		public async Task<List<AccountRow>> ListAccountsAsync()
		{
			await using var db = await _dbFactory.CreateDbContextAsync();
			return await db.Accounts
				.OrderBy(a => a.UserName)
				.Select(a => new AccountRow(
					a.Id, a.UserName, a.Type, a.State, a.LastConnection,
					a.Characters.Count(c => c.Name != null && c.Name != "")))
				.ToListAsync();
		}

		// Ban / unban: Banned <-> Active. Returns the new state, or null if the account is gone.
		public async Task<AccountState?> SetBannedAsync(uint accountId, bool banned)
		{
			await using var db = await _dbFactory.CreateDbContextAsync();
			var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId);
			if (account is null)
				return null;
			account.State = banned ? AccountState.Banned : AccountState.Active;
			await db.SaveChangesAsync();
			return account.State;
		}

		public async Task<AccountType?> SetTypeAsync(uint accountId, AccountType type)
		{
			await using var db = await _dbFactory.CreateDbContextAsync();
			var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId);
			if (account is null)
				return null;
			account.Type = type;
			await db.SaveChangesAsync();
			return account.Type;
		}
	}
}
