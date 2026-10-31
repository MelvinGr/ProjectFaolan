using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.DependencyInjection;

namespace Faolan.Web.Auth
{
	/// <summary>
	///     The signed-in principal for a game account, and the check that keeps it true.
	/// </summary>
	/// <remarks>
	///     The cookie carries what the account was at sign-in for up to its 14-day lifetime, so without the re-check a
	///     banned account stayed signed in and a demoted GM kept the admin pages. Both the cookie
	///     (<see cref="ValidateAsync" />) and an open interactive circuit (<see cref="AccountAuthenticationStateProvider" />)
	///     re-read the account every <see cref="RecheckInterval" />.
	/// </remarks>
	public static class AccountPrincipal
	{
		public const string AdminRole = "Admin";

		public static readonly TimeSpan RecheckInterval = TimeSpan.FromMinutes(1);

		/// <summary>The principal for <paramref name="account" />: its id, its name, and the admin role for a GM or above.</summary>
		public static ClaimsPrincipal Create(Account account)
		{
			var claims = new List<Claim>
			{
				new Claim(ClaimTypes.NameIdentifier, account.Id.ToString()),
				new Claim(ClaimTypes.Name, account.UserName)
			};
			if (IsAdmin(account))
				claims.Add(new Claim(ClaimTypes.Role, AdminRole));

			return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
		}

		/// <summary>
		///     The account, when it may be signed in at all; null when it is gone or not active.
		/// </summary>
		/// <remarks>
		///     The game's own login rule (<c>UniverseAgentListener</c>): a timed ban whose moment has passed lifts first,
		///     then only an active account passes.
		/// </remarks>
		public static async Task<Account?> SignInable(IAccountRepository accounts, uint accountId)
		{
			await accounts.ClearExpiredBan(accountId, DateTimeOffset.UtcNow);
			var account = await accounts.GetAccount(accountId);
			return account is { State: AccountState.Active }
				? account
				: null;
		}

		/// <summary>
		///     Whether <paramref name="user" /> still describes its account: the account may still sign in and holds the
		///     same role. An anonymous user has nothing to keep true.
		/// </summary>
		public static async Task<bool> StillHolds(IAccountRepository accounts, ClaimsPrincipal user)
		{
			if (user.Identity?.IsAuthenticated != true)
				return true;

			if (!uint.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var accountId))
				return false;

			var account = await SignInable(accounts, accountId);
			return account != null && IsAdmin(account) == user.IsInRole(AdminRole);
		}

		/// <summary>
		///     The cookie's <c>OnValidatePrincipal</c>: at most once per <see cref="RecheckInterval" />, re-reads the
		///     account, signs out one that may no longer sign in, and reissues the cookie from the account as it is now.
		/// </summary>
		public static async Task ValidateAsync(CookieValidatePrincipalContext context)
		{
			if (context.Properties.IssuedUtc is { } issued && DateTimeOffset.UtcNow - issued < RecheckInterval)
				return;

			var accounts = context.HttpContext.RequestServices.GetRequiredService<IAccountRepository>();
			var account = uint.TryParse(context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var accountId)
				? await SignInable(accounts, accountId)
				: null;
			if (account == null)
			{
				context.RejectPrincipal();
				await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
				return;
			}

			context.ReplacePrincipal(Create(account));
			context.ShouldRenew = true;
		}

		private static bool IsAdmin(Account account)
		{
			return account.Type >= WebAccountType.GameMaster;
		}
	}
}
