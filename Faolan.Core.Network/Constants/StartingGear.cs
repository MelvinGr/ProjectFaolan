using System.Collections.Generic;
using Faolan.Core.Enums;

namespace Faolan.Core.Network.Constants
{
	// Default starter gear for beach characters (#220): item ids that are fully buildable (p_items identity +
	// harvested hash), so the client accepts them. Harvested from the create-flow captures - every fresh beach
	// character wears the Slave Manacles; the rest of the slave outfit is sex-specific (males bare-chested with a
	// loincloth, females a gown + bottom, colour randomised per character in retail). Character.Sex 0 = male, 1 =
	// female. Moved out of GameServerListener as shared constant data.
	public static class StartingGear
	{
		// Male (Sex 0): loincloth in the legs slot, no chest item.
		public const uint MaleLegsItem = 3805575; // Red Loincloth

		// Universal: the Slave Manacles worn on the wrist (slot 0x12) by every fresh beach character.
		public const uint WristItem = 3361557; // Slave Manacles

		// Female (Sex 1): a gown (chest) + matching bottom (legs). Retail randomises the garment COLOUR per
		// character, so one is picked at random from this slave-gown set (all fully buildable - Hash + Data, Type 101).
		public static readonly (uint Gown, uint Bottom)[] FemaleOutfits =
		[
			(324066, 324176), // Blue
			(324140, 324168), // Purple
			(324067, 324172), // Red
			(324139, 324171), // Silver
			(105844, 324175) // White
		];

		// Per-class starting WEAPON (main-hand-left slot 0x06), harvested from the *create* captures (#220/#276/#286).
		// Coverage is only the classes a create dump actually shows armed - the rest start unarmed until more create
		// captures land. Casters deliberately start unarmed (the earlier Necromancer=Broken Bottle was a login-capture
		// mis-harvest, absent from every create dump); Assassin is the one create-verified entry (Strie).
		public static readonly Dictionary<Class, uint> ClassWeapon = new Dictionary<Class, uint>
		{
			[Class.Assassin] = 3735601, // Marlin Spike
			[Class.Necromancer] = 3395744, // Broken Bottle (0x33D0A0; necro create capture, BccIIR slot-6)
			[Class.TempestOfSet] = 215711 // Broken Bottle (0x34A9F - the OTHER bottle; tempest create capture, BccIIR slot-6)
		};
	}
}
