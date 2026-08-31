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
			if (!Enum.TryParse<AccountType>(e.Value?.ToString(), out var type))
				return;

			var set = await AdminSvc.SetTypeAsync((uint)Id, type);
			_notice = set == null
				? "Account not found."
				: $"Account set to {set}.";
			await Reload();
		}

		// The client's own 25 named bits, in bit order. `None` is not a flag.
		private static readonly GmPrivileges[] NamedBits =
		[
			.. Enum.GetValues<GmPrivileges>().Where(p => p != GmPrivileges.None).OrderBy(p => (uint)p)
		];

		// Used by the shipped roles, never named by the client, and never tested anywhere in it (docs/subsystems/
		// gm-commands.md). Editable so a role can be reproduced exactly rather than approximately.
		private static readonly uint[] UnnamedBits =
			[0x00040000, 0x00200000, 0x00800000, 0x08000000, 0x20000000, 0x40000000, 0x80000000];

		// Funcom's own role masks, verbatim - the point of offering them is that they are policy, not a flag recipe.
		private static readonly (string Name, uint Mask)[] Roles =
		[
			("all", GmRoles.All), ("support2", GmRoles.Support2), ("support1", GmRoles.Support1),
			("operations", GmRoles.Operations), ("dev", GmRoles.Dev), ("internal", GmRoles.Internal),
			("volunteers1", GmRoles.Volunteers1), ("none", 0)
		];

		// What the account is being edited AGAINST: its explicit mask, or the role its type maps to when it has none.
		private uint CurrentMask => _account?.GmPrivileges ?? _account?.Type.ToClientGmLevel() ?? 0;

		// Matched against the RAW role, not the one ToClientGmLevel() hands the wire - that one has GodMode stripped,
		// so comparing against it would never match a role and every account would read "custom".
		private string RoleName => Roles.FirstOrDefault(r => r.Mask == (_account?.Type.ToClientGmLevel(godMode: true) ?? 0)).Name ?? "custom";

		// ⚠️ GodMode is the one bit NOT read from the mask: it lives on the account's `.godmode` toggle so the client
		// and the simulation cannot disagree about it (#902).
		private bool Has(GmPrivileges bit)
		{
			return bit == GmPrivileges.GodMode
				? _account?.GmGodMode ?? false
				: (CurrentMask & (uint)bit) != 0;
		}

		private bool HasRaw(uint bit)
		{
			return (CurrentMask & bit) != 0;
		}

		private Task ToggleBit(GmPrivileges bit, ChangeEventArgs e)
		{
			var on = e.Value is true;

			return bit == GmPrivileges.GodMode
				? SetGodMode(on)
				: SetMask(on
					? CurrentMask | (uint)bit
					: CurrentMask & ~(uint)bit);
		}

		private Task ToggleRaw(uint bit, ChangeEventArgs e)
		{
			return SetMask(e.Value is true
				? CurrentMask | bit
				: CurrentMask & ~bit);
		}

		private Task ApplyPreset(uint mask)
		{
			return SetMask(mask);
		}

		private Task ResetToRole()
		{
			return SetMask(null);
		}

		private async Task SetMask(uint? mask)
		{
			var effective = await AdminSvc.SetGmPrivilegesAsync((uint)Id, mask);
			_notice = effective == null
				? "Account not found."
				: mask == null
					? "GM privileges now inherit the account type's role."
					: $"GM privileges set - gm_level is now 0x{effective:X8}.";
			await Reload();
		}

		private async Task SetGodMode(bool on)
		{
			var effective = await AdminSvc.SetGodModeAsync((uint)Id, on);
			_notice = effective == null
				? "Account not found."
				: on
					? $"God mode ON - the client will ignore health decreases. gm_level is now 0x{effective:X8}."
					: $"God mode off. gm_level is now 0x{effective:X8}.";
			await Reload();
		}

		private async Task ToggleInvisible(ChangeEventArgs e)
		{
			var set = await AdminSvc.SetInvisibleAsync((uint)Id, e.Value is true);
			_notice = set == null
				? "Account not found."
				: $"Invisible to monsters: {(set.Value ? "on" : "off")}.";
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
