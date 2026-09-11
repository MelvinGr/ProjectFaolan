using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Faolan.Core.Extensions;
using Faolan.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Web.Components.Pages
{
	public partial class Mail
	{
		private List<MailView> _mailbox = [];
		private List<string> _characters = [];
		private bool _busy;
		private bool _isAdmin;

		private bool _ready;
		private string _active = "";
		private string _body = "";
		private string _itemId = "";
		private string _money = "";
		private string _qty = "1";
		private string _to = "";
		private string? _notice;
		private ulong? _openId;

		[CascadingParameter]
		private Task<AuthenticationState>? AuthState { get; set; }

		protected override async Task OnInitializedAsync()
		{
			var user = AuthState == null
				? null
				: (await AuthState).User;
			if (user == null || user.Identity?.IsAuthenticated != true || !uint.TryParse(user.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var accountId))
			{
				Nav.NavigateTo("/login", true);
				return;
			}

			_isAdmin = user.IsInRole("Admin");

			await using var db = await DbFactory.CreateDbContextAsync();
			_characters = await db.Characters.Where(c => c.AccountId == accountId && c.Name != null && c.Name != "").OrderBy(c => c.Name).Select(c => c.Name!).ToListAsync();

			if (_characters.Count > 0)
			{
				_active = _characters[0];
				_mailbox = await Mails.GetMailboxAsync(_active);
			}

			_ready = true;
		}

		private async Task Open(MailView m)
		{
			if (_openId == m.MailId)
			{
				_openId = null;
				return;
			}

			_openId = m.MailId;
			if (!m.IsRead)
			{
				await Mails.MarkReadAsync(m.MailId);
				_mailbox = await Mails.GetMailboxAsync(_active); // Refresh the read flag.
			}
		}

		private async Task SelectMailbox(string c)
		{
			_active = c;
			_openId = null;
			_mailbox = await Mails.GetMailboxAsync(c);
		}

		private async Task Send()
		{
			_busy = true;
			ulong money = 0;
			uint itemId = 0, qty = 1;
			if (_isAdmin)
			{
				ulong.TryParse(_money, out money);
				uint.TryParse(_itemId, out itemId);
				if (!uint.TryParse(_qty, out qty) || qty == 0)
					qty = 1;
			}

			var (ok, msg) = await Mails.SendAsync(_to.Trim(), _active, _body, money, itemId, qty);
			_notice = msg;
			if (ok)
			{
				_to = _body = "";
				_money = _itemId = "";
				_qty = "1";
				// Refresh the inbox, in case the mail went to the mailbox being viewed.
				_mailbox = await Mails.GetMailboxAsync(_active);
			}

			_busy = false;
		}

		private static string Money(ulong tin)
		{
			return tin.ToCurrencyString();
		}
	}
}
