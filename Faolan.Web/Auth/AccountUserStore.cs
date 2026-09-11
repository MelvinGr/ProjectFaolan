using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Web.Auth
{
	/// <summary>
	///     A minimal Identity store over the game's Accounts table: just enough for UserManager to create accounts and
	///     check passwords. There is no normalized-name column, so the normalized name is computed on the fly, and the
	///     rest of Identity's surface — lockout, two-factor, claims and so on — is deliberately not implemented.
	/// </summary>
	public sealed class AccountUserStore : IUserStore<Account>, IUserPasswordStore<Account>
	{
		private readonly IDbContextFactory<DatabaseContext> _dbf;

		public AccountUserStore(IDbContextFactory<DatabaseContext> dbf)
		{
			_dbf = dbf;
		}

		public void Dispose()
		{
		}

		public Task<string?> GetPasswordHashAsync(Account user, CancellationToken ct)
		{
			return Task.FromResult<string?>(user.Password);
		}

		public Task<bool> HasPasswordAsync(Account user, CancellationToken ct)
		{
			return Task.FromResult(!string.IsNullOrEmpty(user.Password));
		}

		public Task SetPasswordHashAsync(Account user, string? passwordHash, CancellationToken ct)
		{
			user.Password = passwordHash ?? "";
			return Task.CompletedTask;
		}

		public async Task<IdentityResult> CreateAsync(Account user, CancellationToken ct)
		{
			await using var db = await _dbf.CreateDbContextAsync(ct);
			if (await db.Accounts.AnyAsync(a => a.UserName.ToUpper() == user.UserName.ToUpper(), ct))
			{
				return IdentityResult.Failed(new IdentityError
				{
					Code = "DuplicateUserName",
					Description = "Username already taken."
				});
			}

			db.Accounts.Add(user);
			await db.SaveChangesAsync(ct);
			return IdentityResult.Success;
		}

		public Task<IdentityResult> DeleteAsync(Account user, CancellationToken ct)
		{
			return Task.FromResult(IdentityResult.Failed(new IdentityError
			{
				Code = "NotSupported",
				Description = "Delete is handled by the game, not Identity."
			}));
		}

		public async Task<Account?> FindByIdAsync(string userId, CancellationToken ct)
		{
			if (!uint.TryParse(userId, out var id))
				return null;

			await using var db = await _dbf.CreateDbContextAsync(ct);
			return await db.Accounts.FirstOrDefaultAsync(a => a.Id == id, ct);
		}

		public async Task<Account?> FindByNameAsync(string normalizedUserName, CancellationToken ct)
		{
			await using var db = await _dbf.CreateDbContextAsync(ct);
			return await db.Accounts.FirstOrDefaultAsync(a => a.UserName.ToUpper() == normalizedUserName, ct);
		}

		public Task<string?> GetNormalizedUserNameAsync(Account user, CancellationToken ct)
		{
			return Task.FromResult(user.UserName?.ToUpperInvariant());
		}

		public Task<string> GetUserIdAsync(Account user, CancellationToken ct)
		{
			return Task.FromResult(user.Id.ToString());
		}

		public Task<string?> GetUserNameAsync(Account user, CancellationToken ct)
		{
			return Task.FromResult<string?>(user.UserName);
		}

		/// <summary>
		///     A no-op: there is no normalized-name column, so <see cref="GetNormalizedUserNameAsync" /> computes the
		///     value instead of storing it.
		/// </summary>
		public Task SetNormalizedUserNameAsync(Account user, string? normalizedName, CancellationToken ct)
		{
			return Task.CompletedTask;
		}

		public Task SetUserNameAsync(Account user, string? userName, CancellationToken ct)
		{
			user.UserName = userName ?? "";
			return Task.CompletedTask;
		}

		public async Task<IdentityResult> UpdateAsync(Account user, CancellationToken ct)
		{
			await using var db = await _dbf.CreateDbContextAsync(ct);
			var tracked = await db.Accounts.FirstOrDefaultAsync(a => a.Id == user.Id, ct);
			if (tracked == null)
			{
				return IdentityResult.Failed(new IdentityError
				{
					Code = "NotFound",
					Description = "Account not found."
				});
			}

			// Only the fields this store owns: the password, which is what the live password migration updates, and
			// the username for completeness.
			tracked.Password = user.Password;
			tracked.UserName = user.UserName;
			await db.SaveChangesAsync(ct);
			return IdentityResult.Success;
		}
	}
}
