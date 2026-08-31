using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Web.Services
{
	// One account row for the admin table.
	public sealed record AccountRow(uint Id, string UserName, AccountType Type, AccountState State, DateTimeOffset? LastConnection, int Characters);

	// Full account detail for the per-account admin page.
	public sealed record AccountDetail(
		uint Id,
		string UserName,
		AccountType Type,
		AccountState State,
		DateTimeOffset CreationDate,
		DateTimeOffset? LastConnection,
		string? LastIp,
		List<AccountChar> Characters,

		// The account's own client GM privilege mask, or null when it inherits the role its Type maps to.
		uint? GmPrivileges,
		bool GmGodMode,
		bool GmInvisible,

		// What ClientGmLevel() will actually put on the wire, mask + the GodMode bit from GmGodMode.
		uint EffectiveGmLevel);

	public sealed record AccountChar(uint Id, string Name, ushort Level, string Class, string Race, ulong Money, DateTimeOffset? LastConnection);

	// Admin actions over the shared game Accounts table (ban/unban, account type). Writes go to the same faolan.db the
	// game server reads, so changes take effect on the player's next login (the server enforces State/Type at auth).
	public sealed class AccountAdminService
	{
		private readonly IDbContextFactory<DatabaseContext> _dbFactory;

		public AccountAdminService(IDbContextFactory<DatabaseContext> dbFactory)
		{
			_dbFactory = dbFactory;
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
				: a.LastIpAddress, chars, a.GmPrivileges, a.GmGodMode, a.GmInvisible, a.ClientGmLevel());
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
		public async Task<AccountState?> SetBannedAsync(uint accountId, bool banned)
		{
			await using var db = await _dbFactory.CreateDbContextAsync();
			var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId);
			if (account == null)
				return null;

			account.State = banned
				? AccountState.Banned
				: AccountState.Active;
			await db.SaveChangesAsync();
			return account.State;
		}

		public async Task<AccountType?> SetTypeAsync(uint accountId, AccountType type)
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
		///     Sets the account's explicit client GM privilege mask, or clears it (<paramref name="mask" /> null) so the
		///     account falls back to the role its type maps to. Returns the EFFECTIVE mask that will go on the wire.
		/// </summary>
		/// <remarks>
		///     ⚠️ <see cref="GmPrivileges.GodMode" /> is stripped here and carried by <see cref="SetGodModeAsync" />
		///     instead: it is the one bit that is not a UI permission but client-side invulnerability, and the server's
		///     own `.godmode` has to agree with it or damage stops showing (#902). Storing it in two places is exactly
		///     how they would drift.
		/// </remarks>
		public async Task<uint?> SetGmPrivilegesAsync(uint accountId, uint? mask)
		{
			await using var db = await _dbFactory.CreateDbContextAsync();
			var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId);
			if (account == null)
				return null;

			account.GmPrivileges = mask & ~(uint)Core.Enums.GmPrivileges.GodMode;
			if (mask == null)
				account.GmPrivileges = null;

			await db.SaveChangesAsync();
			return account.ClientGmLevel();
		}

		/// <summary>Sets the persisted god-mode toggle, which is also the client's GodMode privilege bit.</summary>
		public async Task<uint?> SetGodModeAsync(uint accountId, bool on)
		{
			await using var db = await _dbFactory.CreateDbContextAsync();
			var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId);
			if (account == null)
				return null;

			account.GmGodMode = on;
			await db.SaveChangesAsync();
			return account.ClientGmLevel();
		}

		/// <summary>Sets the persisted invisible-to-monsters toggle (server-side; not a client privilege bit).</summary>
		public async Task<bool?> SetInvisibleAsync(uint accountId, bool on)
		{
			await using var db = await _dbFactory.CreateDbContextAsync();
			var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId);
			if (account == null)
				return null;

			account.GmInvisible = on;
			await db.SaveChangesAsync();
			return account.GmInvisible;
		}
	}
}
