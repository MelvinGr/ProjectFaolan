using System;
using System.Linq;
using Faolan.Core.Constants;
using Faolan.Core.Enums;
using Xunit;

namespace Faolan.Tests
{
	// Which guild ranks may withdraw from the guild bank. It was a `[V0]` - the one captured banking session
	// cannot answer it, because the member banking there is that guild's own leader - and the answer turned out to
	// be in the client's shipped data all along: RDB 1000518 `GuildRankDataTable` carries a permission bitmask per
	// rung, and the guild-management view names the bits (`tools/ghidra/DumpGuildRankPermissions.java`).
	//
	// The suite runs from a plain clone, so it cannot re-read the RDB. Asserting the transcription against itself
	// would be worth nothing, so what is gated here is the structure the shipped table has - properties a
	// transcription slip breaks - plus the one derived rule the server now enforces.
	public class GuildRankPermissionTests
	{
		private static readonly GuildGoverningForm[] Forms = Enum.GetValues<GuildGoverningForm>();

		// The six masks the shipped table uses, and nothing else. A mistyped digit produces a seventh value, which is
		// the cheapest possible check on a hand-transcribed column.
		private static readonly uint[] ShippedMasks = [0, 30, 286, 6430, 7518, 0x7FFFFFFF];

		[Fact]
		public void EveryRungCarriesOneOfTheSixShippedMasks()
		{
			foreach (var form in Forms)
				for (byte rank = 0; rank < GuildRankLadder.RungCount(form); rank++)
				{
					var mask = (uint)GuildRankLadder.PermissionsOf(form, rank);
					Assert.True(ShippedMasks.Contains(mask), $"{form} rung {rank} carries 0x{mask:X} which is not one of the shipped masks");
				}
		}

		// The table's own shape: rights only ever accumulate going up a ladder. This is what makes "the top two
		// rungs may withdraw" a statement about a ladder rather than about six unrelated rows.
		[Fact]
		public void PermissionsAreStrictlyNestedUpEachLadder()
		{
			foreach (var form in Forms)
				for (byte rank = 1; rank < GuildRankLadder.RungCount(form); rank++)
				{
					var below = GuildRankLadder.PermissionsOf(form, (byte)(rank - 1));
					var here = GuildRankLadder.PermissionsOf(form, rank);
					Assert.True((here & below) == below, $"{form} rung {rank} (0x{(uint)here:X}) drops a right rung {rank - 1} (0x{(uint)below:X}) holds");
				}
		}

		[Fact]
		public void TheTopRungOfEveryFormHoldsEverything()
		{
			foreach (var form in Forms)
			{
				var leader = GuildRankLadder.LeaderRank(form);
				Assert.Equal(0x7FFFFFFFu, (uint)GuildRankLadder.PermissionsOf(form, leader));
			}
		}

		// The rule the server enforces: the lowest rung that may withdraw. Six forms
		// of seven hand it to the top two rungs; Stygian hands it to the leader alone, because its second rung
		// carries 6430 rather than 7518 and 6430 is the mask without `WithdrawMoney`.
		[Theory]
		[InlineData(GuildGoverningForm.Cimmerian, 3)]      // War Chief
		[InlineData(GuildGoverningForm.Acheron, 4)]        // Deathbringer Envoy
		[InlineData(GuildGoverningForm.Stygian, 3)]        // Emissary of Set - the leader, no deputy tier
		[InlineData(GuildGoverningForm.Aquilonian, 4)]     // Ambassador
		[InlineData(GuildGoverningForm.Nemedian, 4)]       // Commandant
		[InlineData(GuildGoverningForm.HyborianMilita, 4)] // Taxiarchos
		[InlineData(GuildGoverningForm.Harem, 3)]          // Courtesan
		public void TheLowestRungThatMayWithdraw(GuildGoverningForm form, int expected)
		{
			var lowest = Enumerable.Range(0, GuildRankLadder.RungCount(form))
				.FirstOrDefault(r => GuildRankLadder.Allows(form, (byte)r, GuildPermission.WithdrawMoney), -1);

			Assert.Equal(expected, lowest);
			Assert.Equal(form == GuildGoverningForm.Stygian, lowest == GuildRankLadder.LeaderRank(form));
		}

		// Kick/promote/demote reach further down than the bank does - which is the half of the change that stops
		// these being leader-only. Every mask above `None` carries them, so the boundary is "any rung with rights".
		[Fact]
		public void EveryRungWithAnyRightMayInviteKickPromoteAndDemote()
		{
			var officer = GuildPermission.InviteOthers | GuildPermission.KickOthers | GuildPermission.Promote | GuildPermission.Demote;

			foreach (var form in Forms)
				for (byte rank = 0; rank < GuildRankLadder.RungCount(form); rank++)
				{
					var mask = GuildRankLadder.PermissionsOf(form, rank);
					if (mask == GuildPermission.None)
						continue;

					Assert.True((mask & officer) == officer, $"{form} rung {rank} has rights but not the officer four");
				}
		}

		// A form or rung outside the table gets nothing - the caller is deciding whether to let someone drain a bank,
		// so an unknown rung must not fall back to a default set.
		[Fact]
		public void AnUnknownRungHoldsNothing()
		{
			Assert.Equal(GuildPermission.None, GuildRankLadder.PermissionsOf(GuildGoverningForm.Stygian, 99));
			Assert.Equal(GuildPermission.None, GuildRankLadder.PermissionsOf((GuildGoverningForm)42, 0));
			Assert.False(GuildRankLadder.Allows((GuildGoverningForm)42, 0, GuildPermission.WithdrawMoney));
		}
	}
}
