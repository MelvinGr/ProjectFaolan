using System.Collections.Generic;
using System.Collections.Immutable;
using System.Numerics;
using Faolan.Core.Enums;

namespace Faolan.Core.Network.Constants
{
	// Default starter gear for beach characters (#220): item ids that are fully buildable (p_items identity +
	// harvested hash), so the client accepts them. Harvested from the create-flow captures - every fresh beach
	// character wears the Slave Manacles; the rest of the slave outfit is sex-specific (males bare-chested with a
	// loincloth, females a gown + bottom, colour randomised per character in retail). Character.Sex 0 = male, 1 =
	// female. Moved out of GameServerListener as shared constant data.
	public static class StartingData
	{
		// The character-create editor playfield/map id (the scene props are WorldObjects rows on it).
		public const uint EditorMapId = 4000;

		// Male (Sex 0): loincloth in the legs slot, no chest item.
		public const uint StartingMaleLegsItem = 3805575; // Red Loincloth

		// Universal: the Slave Manacles worn on the wrist (slot 0x12) by every fresh beach character.
		public const uint StartingWristItem = 3361557; // Slave Manacles

		public static readonly ImmutableArray<(Race race, ArchType archType, ConanClass clas)> RaceArchClassCombinations =
		[
			(Race.Aquilonian, ArchType.Priest, ConanClass.PriestOfMitra), (Race.Aquilonian, ArchType.Soldier, ConanClass.Conqueror), (Race.Aquilonian, ArchType.Soldier, ConanClass.DarkTemplar),
			(Race.Aquilonian, ArchType.Soldier, ConanClass.Guardian), (Race.Aquilonian, ArchType.Rogue, ConanClass.Barbarian), (Race.Aquilonian, ArchType.Rogue, ConanClass.Ranger),
			(Race.Aquilonian, ArchType.Rogue, ConanClass.Assassin), (Race.Cimmerian, ArchType.Priest, ConanClass.BearShaman), (Race.Cimmerian, ArchType.Soldier, ConanClass.Conqueror),
			(Race.Cimmerian, ArchType.Soldier, ConanClass.DarkTemplar), (Race.Cimmerian, ArchType.Soldier, ConanClass.Guardian), (Race.Cimmerian, ArchType.Rogue, ConanClass.Barbarian),
			(Race.Cimmerian, ArchType.Rogue, ConanClass.Ranger), (Race.Stygian, ArchType.Priest, ConanClass.TempestOfSet), (Race.Stygian, ArchType.Rogue, ConanClass.Ranger),
			(Race.Stygian, ArchType.Rogue, ConanClass.Assassin), (Race.Stygian, ArchType.Mage, ConanClass.HeraldOfXotli), (Race.Stygian, ArchType.Mage, ConanClass.Demonologist),
			(Race.Stygian, ArchType.Mage, ConanClass.Necromancer)
		];

		// Female (Sex 1): a gown (chest) + matching bottom (legs). Retail randomises the garment COLOUR per
		// character, so one is picked at random from this slave-gown set (all fully buildable - Hash + Data, Type 101).
		public static readonly ImmutableArray<(uint Gown, uint Bottom)> StartingFemaleOutfits =
		[
			(324066, 324176), // Blue
			(324140, 324168), // Purple
			(324067, 324172), // Red
			(324139, 324171), // Silver
			(105844, 324175) // White
		];

		// Per-class starting WEAPON (main-hand-left slot 0x06), harvested from the *create* captures (#220/#276/#286).
		// Coverage is only the classes a create dump actually shows armed - the rest start unarmed until more create
		// captures land. The create-verified armed entries are Assassin (Strie), Necromancer and Tempest of Set, each
		// with the class's create-capture main-hand (see the per-entry notes below).
		public static readonly ImmutableDictionary<ConanClass, uint> StartingWeapon = new Dictionary<ConanClass, uint>
		{
			[ConanClass.Assassin] = 3735601, // Marlin Spike
			[ConanClass.Necromancer] = 3395744, // Broken Bottle (0x33D0A0; necro create capture, BccIIR slot-6)
			[ConanClass.TempestOfSet] = 215711 // Broken Bottle (0x34A9F - the OTHER bottle; tempest create capture, BccIIR slot-6)
		}.ToImmutableDictionary();

		public static readonly Vector3 EditorSpawnPosition = new Vector3(260.0f, 0.0f, 290.0f);
	}
}
