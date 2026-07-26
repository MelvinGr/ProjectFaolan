using System;
using System.Linq;
using Faolan.Core.Enums;
using Xunit;

namespace Faolan.Tests
{
	// The GM privilege model (#449): gm_level is a BITMASK, not a level. The values are the client's own - the bits
	// come from FanaticGMLevelCommand_c's constructor and the roles are registered beside them - so these tests pin
	// what was decoded rather than checking arithmetic we invented. docs/subsystems/gm-commands.md has the evidence.
	public class GmPrivilegeTests
	{
		// Every named privilege is exactly one bit. A two-bit entry would mean a mis-transcribed value, and it would
		// fail silently: the client's test is `(gmLevel & required) != 0`, so a wrong bit still "works" for whichever
		// half happens to be set.
		[Fact]
		public void EveryPrivilege_IsASingleBit()
		{
			foreach (var p in Enum.GetValues<GmPrivileges>().Where(p => p != GmPrivileges.None))
				Assert.Equal(1, System.Numerics.BitOperations.PopCount((uint)p));

			Assert.Equal(Enum.GetValues<GmPrivileges>().Length, Enum.GetValues<GmPrivileges>().Distinct().Count());
		}

		// The two roles the client derives rather than states outright, kept as the derivation so a transcription slip
		// in either half shows up here.
		[Fact]
		public void DerivedRoles_MatchWhatTheClientComputes()
		{
			Assert.Equal(GmRoles.Volunteers1, GmRoles.Volunteers2); // registered as a straight copy
			Assert.Equal(0x0951FFEEu, GmRoles.Support2); // support1 | 0x00100C48
			Assert.Equal(GmRoles.Support1 | 0x00100C48u, GmRoles.Support2);
		}

		// The observation that settled the whole model: at gm_level = 10 the retail client refused /kill. Under a
		// LEVEL reading that says "kill needs > 10"; under the mask reading it says 10 simply does not contain
		// NPCCommands - and the same 10 does contain TeleportDynels, so /whereis would have worked.
		[Fact]
		public void TheLevelTenObservation_IsExplainedByTheMask()
		{
			const uint observed = 10;
			Assert.Equal(GmPrivileges.PlayerResurrect | GmPrivileges.TeleportDynels, (GmPrivileges)observed);
			Assert.Equal(0u, observed & (uint)GmPrivileges.NpcCommands); // /kill: refused
			Assert.NotEqual(0u, observed & (uint)GmPrivileges.TeleportDynels); // /whereis: would have been offered
		}

		// A GM must be able to moderate (kick, cloak, GM chat and tag) without the Developer bit, which is what gates
		// the ~85 client debug commands; an Administrator gets everything.
		[Fact]
		public void AccountTypes_MapToRolesWithTheRightReach()
		{
			var gm = AccountType.GameMaster.ToClientGmLevel();
			foreach (var needed in new[] { GmPrivileges.CanKick, GmPrivileges.CanCloak, GmPrivileges.GmChat, GmPrivileges.ShowGmTag, GmPrivileges.GodMode })
				Assert.NotEqual(0u, gm & (uint)needed);

			Assert.Equal(0u, gm & (uint)GmPrivileges.Developer);
			Assert.Equal(GmRoles.All, AccountType.Administrator.ToClientGmLevel());

			// A normal account carries no bit at all - any non-zero value flips the client into GM mode.
			Assert.Equal(0u, AccountType.Normal.ToClientGmLevel());
			Assert.Equal(0u, AccountType.Trial.ToClientGmLevel());
		}
	}
}
