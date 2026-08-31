using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Xunit;

namespace Faolan.Tests
{
	// `Account.ClientGmLevel()` is the ONE place the GM privilege mask on the wire is assembled, now that the admin UI
	// can set bits individually. Two rules it has to keep, and both are load-bearing:
	//
	//  * a per-account mask OVERRIDES the role the account type maps to, and clearing it falls back to that role;
	//  * ⛔ `GodMode` is never taken from the mask. It rides `Account.GmGodMode`, the same flag the simulation reads,
	//    because it is not a UI permission but CLIENT-SIDE INVULNERABILITY: with it set the client discards every
	//    server-driven health decrease (one gate per vital in its stat setter), so an account carrying it while the
	//    server did not think it was in god mode took damage, died, and watched a full health bar (#902). Storing it
	//    in two places is exactly how the two would drift apart again.
	public class AccountGmPrivilegeMaskTests
	{
		private static Account Acc(AccountType type, uint? mask = null, bool godMode = false)
		{
			return new Account { Type = type, GmPrivileges = mask, GmGodMode = godMode };
		}

		[Fact]
		public void NoMask_FallsBackToTheRoleTheTypeMapsTo()
		{
			Assert.Equal(GmRoles.All & ~(uint)GmPrivileges.GodMode, Acc(AccountType.Administrator).ClientGmLevel());
			Assert.Equal(GmRoles.Support2 & ~(uint)GmPrivileges.GodMode, Acc(AccountType.GameMaster).ClientGmLevel());
		}

		[Fact]
		public void AnExplicitMask_OverridesTheRole()
		{
			var only = (uint)(GmPrivileges.CanKick | GmPrivileges.GmChat);

			Assert.Equal(only, Acc(AccountType.GameMaster, only).ClientGmLevel());
		}

		// ⛔ The #902 invariant. Whatever the mask says, the bit is the server's toggle and nothing else.
		[Theory]
		[InlineData(AccountType.GameMaster)]
		[InlineData(AccountType.Administrator)]
		public void GodMode_ComesFromTheServerToggle_NeverFromTheMask(AccountType type)
		{
			var withBit = (uint)(GmPrivileges.CanKick | GmPrivileges.GodMode);

			// asked for in the mask but god mode off -> stripped
			Assert.Equal(0u, Acc(type, withBit).ClientGmLevel() & (uint)GmPrivileges.GodMode);
			// absent from the mask but god mode on -> present
			Assert.NotEqual(0u, Acc(type, (uint)GmPrivileges.CanKick, godMode: true).ClientGmLevel() & (uint)GmPrivileges.GodMode);
			// and the rest of the mask is untouched either way
			Assert.NotEqual(0u, Acc(type, withBit, godMode: true).ClientGmLevel() & (uint)GmPrivileges.CanKick);
		}

		// Any non-zero value flips the retail client into GM mode, so a normal account must never carry one - not via a
		// mask left behind by a demotion, and not via the god-mode toggle.
		[Theory]
		[InlineData(AccountType.Normal)]
		[InlineData(AccountType.Trial)]
		public void ANonGmAccount_CarriesNothing_WhateverIsStored(AccountType type)
		{
			Assert.Equal(0u, Acc(type).ClientGmLevel());
			Assert.Equal(0u, Acc(type, GmRoles.All).ClientGmLevel());
			Assert.Equal(0u, Acc(type, GmRoles.All, godMode: true).ClientGmLevel());
		}

		// A mask of 0 is a real setting ("this GM has no privileges"), not the same as "no mask" - the null case has to
		// stay distinguishable or clearing every box would silently restore the role instead.
		[Fact]
		public void AnExplicitZeroMask_IsNotTheSameAsNoMask()
		{
			Assert.Equal(0u, Acc(AccountType.GameMaster, 0u).ClientGmLevel());
			Assert.NotEqual(0u, Acc(AccountType.GameMaster).ClientGmLevel());
		}
	}
}
