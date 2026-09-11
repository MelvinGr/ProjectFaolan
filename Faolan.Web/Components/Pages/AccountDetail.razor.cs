using System;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Microsoft.AspNetCore.Components;

namespace Faolan.Web.Components.Pages
{
	public partial class AccountDetail
	{
		private Services.AccountDetail? _account;

		private bool _isSelf;
		private string? _notice;

		[Parameter]
		public int Id { get; set; }

		protected override async Task LoadAsync()
		{
			await Reload();
		}

		private async Task ChangeType(ChangeEventArgs e)
		{
			if (_isSelf)
				return;
			if (!Enum.TryParse<WebAccountType>(e.Value?.ToString(), out var type))
				return;

			var set = await AdminSvc.SetTypeAsync((uint)Id, type);
			_notice = set == null
				? "Account not found."
				: $"Account set to {set}.";
			await Reload();
		}

		/// <summary>
		///     The client's own 25 named bits, in bit order. <c>None</c> is not a flag.
		/// </summary>
		private static readonly GmPrivileges[] NamedBits =
		[
			.. Enum.GetValues<GmPrivileges>().Where(p => p != GmPrivileges.None).OrderBy(p => (uint)p)
		];

		/// <summary>
		///     Bits the shipped roles use that the client never names and never tests; see
		///     docs/subsystems/gm-commands.md. They stay editable so a role can be reproduced exactly rather than
		///     approximately.
		/// </summary>
		private static readonly uint[] UnnamedBits =
			[0x00040000, 0x00200000, 0x00800000, 0x08000000, 0x20000000, 0x40000000, 0x80000000];

		/// <summary>
		///     Funcom's own role masks, verbatim. The point of offering them is that they are policy rather than a
		///     flag recipe.
		/// </summary>
		private static readonly (string Name, uint Mask)[] Roles =
		[
			("all", GmRoles.All), ("support2", GmRoles.Support2), ("support1", GmRoles.Support1),
			("operations", GmRoles.Operations), ("dev", GmRoles.Dev), ("internal", GmRoles.Internal),
			("volunteers1", GmRoles.Volunteers1)
		];

		private uint Mask => _account?.GmLevel ?? 0;

		private bool IsGm => _account?.GmLevel is not null;

		private string RoleName => Roles.FirstOrDefault(r => r.Mask == Mask).Name ?? "custom";

		private bool Has(uint bit)
		{
			return (Mask & bit) != 0;
		}

		private Task ToggleBit(uint bit, ChangeEventArgs e)
		{
			return SetMask(e.Value is true
				? Mask | bit
				: Mask & ~bit);
		}

		private Task ApplyPreset(uint mask)
		{
			return SetMask(mask);
		}

		/// <summary>
		///     Revokes GM access. Clearing every bit and "not a GM" are the same state; see
		///     <see cref="Services.AccountAdminService.SetGmLevelAsync" />.
		/// </summary>
		private Task RevokeGm()
		{
			return SetMask(null);
		}

		private Task GrantGm()
		{
			return SetMask(GmRoles.Support2);
		}

		private async Task SetMask(uint? mask)
		{
			var stored = await AdminSvc.SetGmLevelAsync((uint)Id, mask);
			_notice = stored == null
				? "Account is no longer a GM."
				: $"gm_level is now 0x{stored:X8}.";
			await Reload();
		}

		private async Task Reload()
		{
			_account = await AdminSvc.GetAccountAsync((uint)Id);
			_isSelf = _account != null && _account.Id == SelfId;
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
