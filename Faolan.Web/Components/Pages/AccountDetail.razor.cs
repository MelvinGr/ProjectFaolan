using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace Faolan.Web.Components.Pages
{
	public partial class AccountDetail
	{
		private Services.AccountDetail? _account;

		private bool _authorized;
		private bool _isSelf;
		private string? _notice;
		private uint _selfId;

		[Parameter]
		public int Id { get; set; }

		[CascadingParameter]
		private Task<AuthenticationState>? AuthState { get; set; }

		protected override async Task OnInitializedAsync()
		{
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

		private async Task ChangeType(ChangeEventArgs e)
		{
			if (_isSelf)
				return;
			if (!Enum.TryParse<AccountType>(e.Value?.ToString(), out var type))
				return;

			var set = await AdminSvc.SetTypeAsync((uint)Id, type);
			_notice = set == null
				? "Account not found."
				: $"Account set to {set}.";
			await Reload();
		}

		private async Task Reload()
		{
			_account = await AdminSvc.GetAccountAsync((uint)Id);
			_isSelf = _account != null && _account.Id == _selfId;
		}

		private async Task SetBan(bool banned)
		{
			if (banned && _isSelf)
				return;

			var state = await AdminSvc.SetBannedAsync((uint)Id, banned);
			if (state == null)
				_notice = "Account not found.";
			else
			{
				var kicked = banned
					? await Live.KickAccountAsync((uint)Id)
					: 0;
				_notice = $"Account is now {state}." + (kicked > 0
					? $" Kicked {kicked} live session(s)."
					: "");
			}

			await Reload();
		}

		private static string Money(ulong tin)
		{
			return tin.ToCurrencyString();
		}
	}
}
