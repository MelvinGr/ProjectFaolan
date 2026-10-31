using System;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Database.Repositories;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.Extensions.Logging;

namespace Faolan.Web.Auth
{
	/// <summary>
	///     Re-checks the signed-in account while an interactive circuit is open.
	/// </summary>
	/// <remarks>
	///     A circuit keeps the principal it started with, and the cookie's own re-check only runs on an HTTP request, so
	///     an admin page left open would otherwise keep working for an account banned or demoted since. When the account
	///     no longer matches, the circuit's user becomes anonymous and <c>AdminPage</c> leaves the page.
	/// </remarks>
	public sealed class AccountAuthenticationStateProvider : RevalidatingServerAuthenticationStateProvider
	{
		private readonly IAccountRepository _accounts;

		protected override TimeSpan RevalidationInterval => AccountPrincipal.RecheckInterval;

		public AccountAuthenticationStateProvider(ILoggerFactory loggerFactory, IAccountRepository accounts) : base(loggerFactory)
		{
			_accounts = accounts;
		}

		protected override Task<bool> ValidateAuthenticationStateAsync(AuthenticationState authenticationState, CancellationToken cancellationToken)
		{
			return AccountPrincipal.StillHolds(_accounts, authenticationState.User);
		}
	}
}
