using System;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;
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
		private const uint AccountId = 1;

		private static Account Gm(uint mask)
		{
			return new Account { GmLevel = mask };
		}

		private static async Task WithAccount(uint? gmLevel, Func<DatabaseRepository, Task> test)
		{
			var (options, path) = TestDb.NewSeededDatabase(0x01000001);
			try
			{
				await using (var ctx = new DatabaseContext(options))
					await ctx.Accounts.Where(a => a.Id == AccountId).ExecuteUpdateAsync(s => s.SetProperty(a => a.GmLevel, gmLevel), TestContext.Current.CancellationToken);

				await test(new DatabaseRepository(new TestDb.ContextFactory(options)));
			}
			finally
			{
				TestDb.Delete(path);
			}
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
		public async Task GodModeIsJustABit_SoTheClientAndTheServerCannotDisagree()
		{
			await WithAccount((uint)GmPrivileges.CanKick, async repo =>
			{
				var on = Gm((await repo.SetAccountGmPrivilege(AccountId, GmPrivileges.GodMode, true))!.Value);
				Assert.True(on.HasGmPrivilege(GmPrivileges.GodMode));
				Assert.True(on.HasGmPrivilege(GmPrivileges.CanKick)); // the rest of the mask survives

				var off = await repo.SetAccountGmPrivilege(AccountId, GmPrivileges.GodMode, false);
				Assert.Equal((uint)GmPrivileges.CanKick, off);
			});
		}

		// Flipping a bit must never promote: `.godmode` on someone who is not a GM would otherwise quietly make them
		// one, and any non-zero gm_level puts the retail client into GM mode.
		[Fact]
		public async Task SettingAPrivilegeOnANonGm_DoesNothing()
		{
			await WithAccount(null, async repo =>
			{
				Assert.Null(await repo.SetAccountGmPrivilege(AccountId, GmPrivileges.GodMode, true));
				Assert.False((await repo.GetAccount(AccountId))!.IsGm);
			});
		}

		// A toggle changes its one bit in the database, never the whole mask from a session's copy: that copy is as old
		// as the login, so writing it back would re-grant every bit revoked from the web since.
		[Fact]
		public async Task AToggle_KeepsARevocationMadeSinceTheLogin()
		{
			await WithAccount(GmRoles.All, async repo =>
			{
				var session = await repo.GetAccount(AccountId); // the in-world copy, loaded at login
				await repo.SetAccountGmPrivilege(AccountId, GmPrivileges.CanKick, false); // revoked meanwhile

				session!.GmLevel = await repo.SetAccountGmPrivilege(AccountId, GmPrivileges.GodMode, true);

				Assert.False(Gm(session.GmLevel!.Value).HasGmPrivilege(GmPrivileges.CanKick));
				Assert.Equal(session.GmLevel, (await repo.GetAccount(AccountId))!.GmLevel);
			});
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
