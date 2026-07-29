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

		// The 0x205A PerformSpecialAction selector that toggles a caster's SPELLWEAVE, per class. It is NOT one shared
		// id: the necromancer's activate/deactivate is 0xFC and the demonologist's is 0x10C, and each class uses only
		// its own - across the whole corpus the three NecroL80 (necromancer) spellweave captures carry 0xFC and never
		// 0x10C, while both DemoL80 (demonologist) captures carry 0x10C and never 0xFC.
		//
		// What proves they are the SAME mechanic rather than two unrelated actions is the server's answer: both produce
		// the identical player BehaviorControlCenterIIR pair - behaviour 42041 to open the trance, then 41302 to close
		// it - with the same slot 1 and form 4. Two selectors, one behaviour pair.
		//
		// Priest of Mitra and Tempest of Set also weave (docs/subsystems/combos.md) but no capture shows either one
		// doing it, so they are deliberately absent rather than guessed: an unmapped class logs and does nothing, which
		// is visible, where a wrong id would silently start a weave on the wrong input.
		public static readonly ImmutableDictionary<ConanClass, uint> SpellweaveToggleAction = new Dictionary<ConanClass, uint>
		{
			[ConanClass.Necromancer] = 0xFC,
			[ConanClass.Demonologist] = 0x10C
		}.ToImmutableDictionary();

		// Each race's HOME city - where the Totem of Origins sends you. This is a DIFFERENT destination from the bind
		// point: the Path of Asura spell recalls to the bound location (the bindstone/Wayfarer point the player last
		// set), while the Totem returns you to your race's origin regardless of where you are bound. Both used to
		// funnel into the same bind recall, which was wrong for the Totem.
		//
		// Confirmed for Stygian from the use_totem_of_origin capture (AssassinL23, rogue-assassin): the player uses the
		// Totem while standing in Old Tarantia at (1001.8, 115.3, 618.8) - ~57 u from Old Tarantia's own respawn point,
		// so a bind recall would have been a no-op - and lands on map 3000 (Khemi) at (759.0, 165.4, 1093.0), 22 u from
		// Khemi's AlwaysKnown "Akhet Docks" point. Khemi is the Stygian city, and the character was neither bound nor
		// standing there. The other three rows are the same rule applied to each race's origin city; only the Stygian
		// one is capture-verified, hence the ticket stays needs-testing.
		public static readonly ImmutableDictionary<Race, uint> RacialHomeMap = new Dictionary<Race, uint>
		{
			[Race.Cimmerian] = 1000, // Conarch Village
			[Race.Aquilonian] = 2000, // Old Tarantia
			[Race.Stygian] = 3000, // Khemi          (capture-verified)
			[Race.Khitan] = 6400 // Paikang        (Rise of the Godslayer)
		}.ToImmutableDictionary();

		// Which classes each race may create, straight from the client's own RDB 1000507 race x class table (record 0,
		// the shipped set - see docs/rdb/client-data-tables.md). It replaces a hand-authored list that was close but
		// not complete: it was missing Assassin for Cimmerians, Necromancer for Aquilonians, Dark Templar for
		// Stygians, and the whole Khitan race.
		//
		// The ARCHETYPE is not in 1000507 - that table is (race, class) pairs and lists the five archetypes as
		// entries of their own rather than as parents - so the class -> archetype mapping below is ours, unchanged
		// from the previous list.
		public static readonly ImmutableArray<(Race race, ArchType archType, ConanClass clas)> RaceArchClassCombinations =
		[
			// Cimmerian - no Mage archetype at all, which is what identified this race as 1 rather than 2.
			(Race.Cimmerian, ArchType.Soldier, ConanClass.Guardian), (Race.Cimmerian, ArchType.Soldier, ConanClass.Conqueror), (Race.Cimmerian, ArchType.Soldier, ConanClass.DarkTemplar),
			(Race.Cimmerian, ArchType.Priest, ConanClass.BearShaman), (Race.Cimmerian, ArchType.Rogue, ConanClass.Barbarian), (Race.Cimmerian, ArchType.Rogue, ConanClass.Ranger),
			(Race.Cimmerian, ArchType.Rogue, ConanClass.Assassin),

			// Aquilonian
			(Race.Aquilonian, ArchType.Soldier, ConanClass.Guardian), (Race.Aquilonian, ArchType.Soldier, ConanClass.Conqueror), (Race.Aquilonian, ArchType.Soldier, ConanClass.DarkTemplar),
			(Race.Aquilonian, ArchType.Priest, ConanClass.PriestOfMitra), (Race.Aquilonian, ArchType.Rogue, ConanClass.Barbarian), (Race.Aquilonian, ArchType.Rogue, ConanClass.Ranger),
			(Race.Aquilonian, ArchType.Rogue, ConanClass.Assassin), (Race.Aquilonian, ArchType.Mage, ConanClass.Necromancer),

			// Stygian - the only race with no Soldier archetype entry in 1000507, yet it does get Dark Templar.
			(Race.Stygian, ArchType.Soldier, ConanClass.DarkTemplar), (Race.Stygian, ArchType.Priest, ConanClass.TempestOfSet), (Race.Stygian, ArchType.Rogue, ConanClass.Ranger),
			(Race.Stygian, ArchType.Rogue, ConanClass.Assassin), (Race.Stygian, ArchType.Mage, ConanClass.HeraldOfXotli), (Race.Stygian, ArchType.Mage, ConanClass.Demonologist),
			(Race.Stygian, ArchType.Mage, ConanClass.Necromancer),

			// Khitan (Rise of the Godslayer) - no Barbarian, no Conqueror, no Priest of Mitra, no Tempest of Set.
			(Race.Khitan, ArchType.Soldier, ConanClass.Guardian), (Race.Khitan, ArchType.Soldier, ConanClass.DarkTemplar), (Race.Khitan, ArchType.Priest, ConanClass.BearShaman),
			(Race.Khitan, ArchType.Rogue, ConanClass.Ranger), (Race.Khitan, ArchType.Rogue, ConanClass.Assassin), (Race.Khitan, ArchType.Mage, ConanClass.HeraldOfXotli),
			(Race.Khitan, ArchType.Mage, ConanClass.Demonologist), (Race.Khitan, ArchType.Mage, ConanClass.Necromancer)
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
