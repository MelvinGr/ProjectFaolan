using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Xunit;

namespace Faolan.Tests
{
	// `Account.GmLevel` is the account's whole GM state: the retail client's `gm_level` mask, or null for an account
	// that is not a GM. It is the single store for every privilege, including `GodMode` (0x200) and
	// `InvisibleToMonsters` (0x80), because two stores of one fact drift: the client discards every server-driven
	// health decrease while `GodMode` is set, so an account carrying the bit while the server thought otherwise takes
	// damage, dies, and watches a full health bar the whole time.
	//
	// It is independent of `WebAccountType`, which governs the admin site and says nothing about the game.
	public class AccountGmPrivilegeMaskTests
	{
		private static Account Gm(uint mask)
		{
			return new Account { GmLevel = mask };
		}

		[Fact]
		public void NoLevel_IsNotAGm_AndPutsZeroOnTheWire()
		{
			var account = new Account();

			Assert.False(account.IsGm);
			Assert.Equal(0u, account.ClientGmLevel());
			Assert.False(account.HasGmPrivilege(GmPrivileges.CanKick));
		}

		[Fact]
		public void TheStoredMaskIsWhatGoesOnTheWire()
		{
			Assert.Equal(GmRoles.Support2, Gm(GmRoles.Support2).ClientGmLevel());
			Assert.True(Gm(GmRoles.Support2).IsGm);
			Assert.True(Gm(GmRoles.Support2).HasGmPrivilege(GmPrivileges.CanKick));
			Assert.False(Gm(GmRoles.Support2).HasGmPrivilege(GmPrivileges.Developer));
		}

		// The invariant is structural rather than enforced: god mode is a bit, so the client's invulnerability and the
		// server's `.godmode` are the same fact and cannot disagree.
		[Fact]
		public void GodModeIsJustABit_SoTheClientAndTheServerCannotDisagree()
		{
			var account = Gm((uint)GmPrivileges.CanKick);
			Assert.False(account.HasGmPrivilege(GmPrivileges.GodMode));

			account.SetGmPrivilege(GmPrivileges.GodMode, true);
			Assert.True(account.HasGmPrivilege(GmPrivileges.GodMode));
			Assert.NotEqual(0u, account.ClientGmLevel() & (uint)GmPrivileges.GodMode);
			Assert.True(account.HasGmPrivilege(GmPrivileges.CanKick)); // the rest of the mask survives

			account.SetGmPrivilege(GmPrivileges.GodMode, false);
			Assert.Equal(0u, account.ClientGmLevel() & (uint)GmPrivileges.GodMode);
		}

		// Flipping a bit must never promote: `.godmode` on someone who is not a GM would otherwise quietly make them
		// one, and any non-zero gm_level puts the retail client into GM mode.
		[Fact]
		public void SettingAPrivilegeOnANonGm_DoesNothing()
		{
			var account = new Account();

			account.SetGmPrivilege(GmPrivileges.GodMode, true);

			Assert.False(account.IsGm);
			Assert.Equal(0u, account.ClientGmLevel());
		}

		// The web account type is the admin site's role. It must not leak into the game either way round.
		[Theory]
		[InlineData(WebAccountType.Trial)]
		[InlineData(WebAccountType.Normal)]
		[InlineData(WebAccountType.GameMaster)]
		[InlineData(WebAccountType.Administrator)]
		public void TheWebAccountType_DoesNotDecideGmPrivileges(WebAccountType type)
		{
			Assert.Equal(0u, new Account { Type = type }.ClientGmLevel());
			Assert.Equal(GmRoles.Dev, new Account { Type = type, GmLevel = GmRoles.Dev }.ClientGmLevel());
		}
	}
}
