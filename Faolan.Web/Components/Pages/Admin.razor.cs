using System.Security.Claims;
using Faolan.Core.Enums;
using Faolan.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace Faolan.Web.Components.Pages
{
	public partial class Admin
	{
		private bool _authorized;
		private List<AccountRow> _accounts = [];
		private string? _notice;
		private uint _selfId;

		[CascadingParameter]
		private Task<AuthenticationState>? AuthState { get; set; }

		protected override async Task OnInitializedAsync()
		{
			// Gate: GM/Administrator only (the "Admin" role is stamped at sign-in from the account type).
			var user = AuthState == null
				? null
				: (await AuthState).User;
			if (user == null || !user.IsInRole("Admin"))
			{
				Nav.NavigateTo("/", true);
				return;
			}

			uint.TryParse(user.FindFirst(ClaimTypes.NameIdentifier)?.Value, out _selfId);
			_authorized = true;
			await Reload();
		}

		private async Task ChangeType(uint id, string? value)
		{
			if (id == _selfId)
				return; // don't change your own privileges
			if (!Enum.TryParse<AccountType>(value, out var type))
				return;

			var set = await AdminSvc.SetTypeAsync(id, type);
			_notice = set == null
				? "Account not found."
				: $"Account {Name(id)} set to {set}.";
			await Reload();
		}

		private void ClearCache()
		{
			Models.ClearRenderCache();
			_notice = "Render cache cleared (item models + map images).";
		}

		private string Name(uint id)
		{
			return _accounts.FirstOrDefault(a => a.Id == id)?.UserName ?? id.ToString();
		}

		private async Task Reload()
		{
			_accounts = await AdminSvc.ListAccountsAsync();
		}

		private async Task SetBan(uint id, bool banned)
		{
			if (banned && id == _selfId)
				return; // never ban yourself

			var state = await AdminSvc.SetBannedAsync(id, banned);
			if (state == null)
				_notice = "Account not found.";
			else
			{
				// A ban blocks the next login; also kick any live session so it takes effect immediately.
				var kicked = banned
					? await Live.KickAccountAsync(id)
					: 0;
				_notice = $"Account {Name(id)} is now {state}." + (kicked > 0
					? $" Kicked {kicked} live session(s)."
					: "");
			}

			await Reload();
		}
	}
}
