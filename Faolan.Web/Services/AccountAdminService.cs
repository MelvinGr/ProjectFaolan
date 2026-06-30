using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Web.Services
{
	// One account row for the admin table.
	public sealed record AccountRow(
		uint Id, string UserName, AccountType Type, AccountState State, DateTime? LastConnection, int Characters);

	// Full account detail for the per-account admin page.
	public sealed record AccountDetail(
		uint Id, string UserName, AccountType Type, AccountState State,
		DateTime CreationDate, DateTime? LastConnection, string? LastIp, List<AccountChar> Characters);

	public sealed record AccountChar(
		uint Id, string Name, ushort Level, string Class, string Race, ulong Money, DateTimeOffset? LastConnection);

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

		public async Task<AccountDetail?> GetAccountAsync(uint id)
		{
			await using var db = await _dbFactory.CreateDbContextAsync();
			var a = await db.Accounts.Include(x => x.Characters).FirstOrDefaultAsync(x => x.Id == id);
			if (a is null)
				return null;

			var chars = a.Characters
				.Where(c => !string.IsNullOrEmpty(c.Name))
				.OrderByDescending(c => c.Level).ThenBy(c => c.Name)
				.Select(c => new AccountChar((uint)c.Id, c.Name, c.Level, c.Class.ToString(), c.Race.ToString(),
					c.Money, c.LastConnection))
				.ToList();

			return new AccountDetail(a.Id, a.UserName, a.Type, a.State, a.CreationDate, a.LastConnection,
				string.IsNullOrEmpty(a.LastIpAddress) ? null : a.LastIpAddress, chars);
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
