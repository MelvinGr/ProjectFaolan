using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Web.Services
{
	// One account row for the admin table.
	public sealed record AccountRow(uint Id, string UserName, WebAccountType Type, AccountState State, DateTimeOffset? LastConnection, int Characters);

	// Full account detail for the per-account admin page.
	public sealed record AccountDetail(
		uint Id,
		string UserName,
		WebAccountType Type,
		AccountState State,
		DateTimeOffset CreationDate,
		DateTimeOffset? LastConnection,
		string? LastIp,
		List<AccountChar> Characters,

		// The account's GM privilege mask, or null when it is not a GM at all. This IS what goes on the wire.
		uint? GmLevel);

	public sealed record AccountChar(uint Id, string Name, ushort Level, string Class, string Race, ulong Money, DateTimeOffset? LastConnection);

	// Admin actions over the shared game Accounts table (ban/unban, account type). Writes go to the same faolan.db the
	// game server reads, so changes take effect on the player's next login (the server enforces State/Type at auth).
	public sealed class AccountAdminService
	{
		private readonly IAccountRepository _accounts;
		private readonly IDbContextFactory<DatabaseContext> _dbFactory;

		public AccountAdminService(IDbContextFactory<DatabaseContext> dbFactory, IAccountRepository accounts)
		{
			_dbFactory = dbFactory;
			_accounts = accounts;
		}

		public async Task<AccountDetail?> GetAccountAsync(uint id)
		{
			await using var db = await _dbFactory.CreateDbContextAsync();
			var a = await db.Accounts.Include(x => x.Characters).FirstOrDefaultAsync(x => x.Id == id);
			if (a == null)
				return null;

			var chars = a
				.Characters.Where(c => !string.IsNullOrEmpty(c.Name))
				.OrderByDescending(c => c.Level)
				.ThenBy(c => c.Name)
				.Select(c => new AccountChar(c.Id, c.Name!, c.Level, c.Class.ToString(), c.Race.ToString(), c.Money, c.LastConnection))
				.ToList();

			return new AccountDetail(a.Id, a.UserName, a.Type, a.State, a.CreationDate, a.LastConnection, string.IsNullOrEmpty(a.LastIpAddress)
				? null
				: a.LastIpAddress, chars, a.GmLevel);
		}

		public async Task<List<AccountRow>> ListAccountsAsync()
		{
			await using var db = await _dbFactory.CreateDbContextAsync();
			return await db
				.Accounts.OrderBy(a => a.UserName)
				.Select(a => new AccountRow(a.Id, a.UserName, a.Type, a.State, a.LastConnection, a.Characters.Count(c => c.Name != null && c.Name != "")))
				.ToListAsync();
		}

		// Ban / unban: Banned <-> Active. Returns the new state, or null if the account is gone.
		//
		// Through the repository's state change, which clears any timed ban's expiry with it: a ban set here is
		// indefinite, and writing only the state left a `.kickban`'s expiry behind, so the next game login lifted it.
		public async Task<AccountState?> SetBannedAsync(uint accountId, bool banned)
		{
			var state = banned
				? AccountState.Banned
				: AccountState.Active;
			return await _accounts.SetAccountState(accountId, state)
				? state
				: null;
		}

		public async Task<WebAccountType?> SetTypeAsync(uint accountId, WebAccountType type)
		{
			await using var db = await _dbFactory.CreateDbContextAsync();
			var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId);
			if (account == null)
				return null;

			account.Type = type;
			await db.SaveChangesAsync();
			return account.Type;
		}

		/// <summary>
		///     Sets the account's GM privilege mask, or clears it (<paramref name="mask" /> null) so the account stops
		///     being a GM. Returns the stored value.
		/// </summary>
		/// <remarks>
		///     An all-zero mask is normalised to null. On the wire the two are the same thing - any non-zero value flips
		///     the retail client into GM mode, so "a GM with no privileges" cannot be expressed - and keeping a 0 around
		///     would be a second way to say "not a GM" that <c>IsGm</c> would disagree with.
		/// </remarks>
		public async Task<uint?> SetGmLevelAsync(uint accountId, uint? mask)
		{
			await using var db = await _dbFactory.CreateDbContextAsync();
			var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId);
			if (account == null)
				return null;

			account.GmLevel = mask is null or 0
				? null
				: mask;
			await db.SaveChangesAsync();
			return account.GmLevel;
		}

	}
}
