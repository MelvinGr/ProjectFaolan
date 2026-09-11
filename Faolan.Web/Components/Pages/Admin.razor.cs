using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Enums;
using Faolan.Web.Services;

namespace Faolan.Web.Components.Pages
{
	public partial class Admin
	{
		private List<AccountRow> _accounts = [];
		private string? _notice;

		protected override async Task LoadAsync()
		{
			await Reload();
		}

		private async Task ChangeType(uint id, string? value)
		{
			if (id == SelfId)
				return; // An account cannot change its own privileges.
			if (!Enum.TryParse<WebAccountType>(value, out var type))
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
			if (banned && id == SelfId)
				return; // An account cannot ban itself.

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
