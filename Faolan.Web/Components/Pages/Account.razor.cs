using System.Security.Claims;
using Faolan.Core.Database.Tables;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Web.Components.Pages
{
	public partial class Account
	{
		private Core.Database.Tables.Account? _account;
		private List<Character> _characters = [];

		[CascadingParameter]
		private Task<AuthenticationState>? AuthState { get; set; }

		protected override async Task OnInitializedAsync()
		{
			// Account-only page: bounce to login when signed out (Routes uses a plain RouteView, so no [Authorize] gate).
			if (AuthState == null || (await AuthState).User is not { Identity.IsAuthenticated: true } user || !uint.TryParse(user.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var accountId))
			{
				Nav.NavigateTo("/login", true);
				return;
			}

			await using var db = await DbFactory.CreateDbContextAsync();
			_account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId);
			if (_account == null)
			{
				Nav.NavigateTo("/login", true);
				return;
			}

			_characters = await db.Characters.Where(c => c.AccountId == accountId && c.Name != null && c.Name != "").OrderByDescending(c => c.Level).ThenBy(c => c.Name).ToListAsync();
		}
	}
}
