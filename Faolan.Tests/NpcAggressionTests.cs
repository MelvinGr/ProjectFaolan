using System.Collections.Generic;
using Faolan.Core.Database.Interfaces;
using Faolan.Core.Database.Tables;
using Faolan.GameServer.Combat;
using Xunit;

namespace Faolan.Tests
{
	// The Town Crier attacked the player. He is `cCategory = 1` (hostile) unanimously - 62 votes across two
	// captures and two characters - and he never fights: he spawns and despawns as the player walks past him for a
	// whole session with no attack, no hate list, no threat. Our code read one flag as both the name colour and the
	// decision to aggro, so retail colouring a civilian red made him attack.
	//
	// Neither of the obvious discriminators works on its own, which is why the rule is a conjunction:
	//
	//   * perception - the crier has null Sight/Hearing/Smelling, a distinctive 7% of hostiles. But that 7% also
	//     holds the Crow gang, the Wharf Rats and the Nemedian Scouts, which are genuinely aggressive.
	//   * combat capability alone - 102 hostile templates carry no weapon and most are animals, which fight fine
	//     without one.
	//
	// Armed-or-not-humanoid separates them, and it was validated on the exact population that killed perception:
	// of the 18 senseless hostiles, all 14 real mobs are armed and all 5 civilians are unarmed.
	public class NpcAggressionTests
	{
		private const uint OneHandEdge = 30011, Bow = 30017, Necklace = 30000;

		private static INpcEquipment Worn(uint pos, uint category)
		{
			return new NpcSpawnEquipment
			{
				Pos = pos,
				Item = new Item { SearchCategory = category }
			};
		}

		// The reported case: a humanoid with nothing in either hand does not start a fight.
		[Fact]
		public void AnUnarmedHumanoid_DoesNotInitiate()
		{
			Assert.False(NpcWeapon.Initiates([], humanoid: true));
		}

		// The trap that would have made the rule select nothing at all: the crier's own equipment rows are not
		// empty - they hold a necklace, shoulders and legs. "The hand slot holds something" would call him armed.
		[Fact]
		public void AHumanoidWearingOnlyArmour_DoesNotInitiate()
		{
			List<INpcEquipment> kit = [Worn(NpcWeapon.MainHandPos, Necklace), Worn(NpcWeapon.OffHandPos, Necklace)];

			Assert.False(NpcWeapon.Initiates(kit, humanoid: true));
		}

		// The 14 real mobs in the senseless population - armed, and they must stay aggressive.
		[Theory]
		[InlineData(OneHandEdge)]
		[InlineData(Bow)]
		public void AnArmedHumanoid_Initiates(uint category)
		{
			Assert.True(NpcWeapon.Initiates([Worn(NpcWeapon.MainHandPos, category)], humanoid: true));
		}

		// An off-hand weapon counts too - a dual-wielder with an empty main hand is still armed.
		[Fact]
		public void AWeaponInTheOffHandAlone_Initiates()
		{
			Assert.True(NpcWeapon.Initiates([Worn(NpcWeapon.OffHandPos, OneHandEdge)], humanoid: true));
		}

		// And the half that keeps the rule from muting the wildlife: an animal needs no weapon to mean it. This is
		// what separates a wolf from a Dockworker, and what keeps `Frozen Hatred` and `Parasitic Soul` - both unarmed
		// and both real mobs - aggressive.
		[Fact]
		public void ANonHumanoid_InitiatesUnarmed()
		{
			Assert.True(NpcWeapon.Initiates([], humanoid: false));
		}

		// The default has to be aggressive, so a mint site that never thinks about it still produces a hostile NPC.
		// A default of false would silently pacify every NPC spawned by a path that forgot - the opposite failure,
		// and a much quieter one.
		[Fact]
		public void AFreshEntity_IsAggressiveUntilToldOtherwise()
		{
			Assert.True(new Faolan.GameServer.World.WorldEntity(1, 1, false, "x", null!).Aggressive);
		}
	}
}
