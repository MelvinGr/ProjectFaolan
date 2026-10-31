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
		Task<bool> SetAccountAuthStatus(uint accountId, uint authStatus);
		Task<bool> SetAccountClientInstance(uint accountId, uint clientInstance);

		/// <summary>
		///     Turns one GM privilege on or off in place and returns the account's mask as stored afterwards (null: not a
		///     GM, which a bit never changes). Every other bit is left as the database holds it.
		/// </summary>
		/// <remarks>
		///     Not a write of the caller's whole mask: a session's copy of the account is as old as its login, so writing it
		///     back would undo whatever changed the mask since, a revocation from the web above all.
		/// </remarks>
		Task<uint?> SetAccountGmPrivilege(uint accountId, GmPrivileges privilege, bool on);
		Task SetAccountClaimQuantity(uint claimId, uint? quantity);
		Task SetAccountClaimsAvailable(uint accountId, bool available);
		Task<bool> SetAccountAuthChallenge(uint accountId, Guid challenge);

		/// <summary>Stores the cookie a successful login was issued, replacing the previous login's.</summary>
		Task<bool> SetAccountLoginCookie(uint accountId, uint cookie);

		/// <summary>
		///     The account, when <paramref name="cookie" /> is the one its current login was issued; null otherwise, which
		///     a handshake must refuse.
		/// </summary>
		Task<Account?> GetAccountForLogin(uint accountId, uint cookie);
		Task<bool> SetAccountLastInfo(uint accountId, string ipAddress);
		Task<bool> SetAccountState(uint accountId, AccountState state);

		/// <summary>Bans an account until <paramref name="expiresAt" />, in one write.</summary>
		Task<bool> BanUntil(uint accountId, DateTimeOffset expiresAt);

		/// <summary>
		///     Lifts a timed ban whose moment has passed; true when one actually lifted. An indefinite ban
		///     (<c>BanExpiresAt</c> null) is never touched.
		/// </summary>
		Task<bool> ClearExpiredBan(uint accountId, DateTimeOffset now);
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

		// Case-insensitive, matching how the web login has always looked accounts up (Identity's FindByNameAsync
		// normalises to upper-case). This was an exact match, so the two logins disagreed: the web let you in as
		// "Faolan" when the row said "faolan" and the game did not, for the same account and the same password.
		// Usernames are alphanumeric ASCII, so upper() folding is exact here.
		//
		// The unique index on UserName is still binary collation, so the database would accept "Faolan" and
		// "faolan" as two rows even though nothing can now tell them apart. Nothing creates such a pair today -
		// registration has always checked case-insensitively - but making the index nocase is the real fix.
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

		public async Task<bool> SetAccountAuthStatus(uint accountId, uint authStatus)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Accounts.Where(a => a.Id == accountId).ExecuteUpdateAsync(s => s.SetProperty(a => a.AuthStatus, authStatus)) == 1;
		}

		public async Task<bool> SetAccountClientInstance(uint accountId, uint clientInstance)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Accounts.Where(a => a.Id == accountId).ExecuteUpdateAsync(s => s.SetProperty(a => a.ClientInstance, clientInstance)) == 1;
		}

		public async Task<uint?> SetAccountGmPrivilege(uint accountId, GmPrivileges privilege, bool on)
		{
			var bit = (uint)privilege;
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var accounts = ctx.Accounts.Where(a => a.Id == accountId && a.GmLevel != null);
			if (on)
				await accounts.ExecuteUpdateAsync(s => s.SetProperty(a => a.GmLevel, a => a.GmLevel | bit));
			else
				await accounts.ExecuteUpdateAsync(s => s.SetProperty(a => a.GmLevel, a => a.GmLevel & ~bit));

			return await ctx.Accounts.Where(a => a.Id == accountId).Select(a => a.GmLevel).FirstOrDefaultAsync();
		}

		// The account-wide client settings archive. Its own method, not a field on the login-step writes: those run on
		// nearly every login step, and rewriting a 1 KB blob each time would be pure churn.
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

		// Rotates the login nonce. Called on every Authenticate before the challenge goes out, so a login block
		// captured off the wire can never be replayed against the next attempt.
		public async Task<bool> SetAccountAuthChallenge(uint accountId, Guid challenge)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Accounts.Where(a => a.Id == accountId).ExecuteUpdateAsync(s => s.SetProperty(a => a.AuthChallenge, challenge)) == 1;
		}

		public async Task<bool> SetAccountLoginCookie(uint accountId, uint cookie)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Accounts.Where(a => a.Id == accountId).ExecuteUpdateAsync(s => s.SetProperty(a => a.LoginCookie, cookie)) == 1;
		}

		public async Task<Account?> GetAccountForLogin(uint accountId, uint cookie)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Accounts.FirstOrDefaultAsync(a => a.Id == accountId && a.LoginCookie == cookie);
		}

		public async Task<bool> SetAccountLastInfo(uint accountId, string ipAddress)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Accounts.Where(a => a.Id == accountId).ExecuteUpdateAsync(s => s.SetProperty(a => a.LastConnection, DateTimeOffset.UtcNow).SetProperty(a => a.LastIpAddress, ipAddress)) ==
			       1;
		}

		// Clears any timed-ban expiry alongside the state. Every state change is a decision that supersedes
		// a pending lift: `.unban` obviously, but `.ban` too - an indefinite ban must not inherit a `.kickban`'s
		// leftover expiry and silently lift itself. `BanUntil` is the one path that sets one.
		public async Task<bool> SetAccountState(uint accountId, AccountState state)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Accounts.Where(a => a.Id == accountId)
				.ExecuteUpdateAsync(s => s.SetProperty(a => a.State, state).SetProperty(a => a.BanExpiresAt, (DateTimeOffset?)null)) == 1;
		}

		// A timed ban: banned now, lifting itself at `expiresAt`. One write, so the state and its expiry can
		// never disagree.
		public async Task<bool> BanUntil(uint accountId, DateTimeOffset expiresAt)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Accounts.Where(a => a.Id == accountId)
				.ExecuteUpdateAsync(s => s.SetProperty(a => a.State, AccountState.Banned).SetProperty(a => a.BanExpiresAt, expiresAt)) == 1;
		}

		// Lifts a timed ban whose moment has passed. Returns true when it actually lifted one, so the caller can log
		// the transition rather than guess at it.
		//
		// This is what makes the lift survive a restart in both directions - a server that was down through the
		// whole window lifts on the next login attempt, and one that comes back inside the window still holds the ban.
		// Scoped to `Banned` rows with an expiry that has elapsed, so an indefinite ban (`BanExpiresAt` null) can
		// never be caught by it.
		public async Task<bool> ClearExpiredBan(uint accountId, DateTimeOffset now)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();

			// The expiry is compared here rather than in the Where clause, and that is a provider limit rather than
			// a preference: SQLite cannot translate a `DateTimeOffset` ordering (it stores one as text), so
			// `a.BanExpiresAt <= now` inside `ExecuteUpdate` throws "could not be translated" at runtime - a failure
			// that only appears when the path is actually taken. Reading the row first costs one query on a path that
			// already loads the account, and keeps the column the same `DateTimeOffset?` as its neighbours.
			var expiry = await ctx.Accounts.Where(a => a.Id == accountId && a.State == AccountState.Banned)
				.Select(a => a.BanExpiresAt)
				.FirstOrDefaultAsync();
			if (expiry == null || expiry > now)
				return false;

			// Still scoped to Banned rows carrying an expiry, so a state change that landed between the read and this
			// write cannot be undone by it.
			return await ctx.Accounts
				.Where(a => a.Id == accountId && a.State == AccountState.Banned && a.BanExpiresAt != null)
				.ExecuteUpdateAsync(s => s.SetProperty(a => a.State, AccountState.Active).SetProperty(a => a.BanExpiresAt, (DateTimeOffset?)null)) == 1;
		}
	}
}
