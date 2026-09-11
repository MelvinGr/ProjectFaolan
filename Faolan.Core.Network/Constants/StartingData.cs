using System.Collections.Generic;
using System.Collections.Immutable;
using System.Numerics;
using Faolan.Core.Enums;

namespace Faolan.Core.Network.Constants
{
	/// <summary>
	///     Shared constant data for a fresh beach character: starter gear, the create editor's scene, and the small
	///     per-class/per-race tables the create and stance paths read.
	/// </summary>
	/// <remarks>
	///     The gear item ids are fully buildable (p_items identity plus harvested hash), so the client accepts them.
	///     Harvested from the create-flow captures: every fresh beach character wears the Slave Manacles, and the rest
	///     of the slave outfit is sex-specific - males bare-chested with a loincloth, females a gown plus bottom with
	///     the colour randomised per character in retail. <c>Character.Sex</c> 0 = male, 1 = female.
	/// </remarks>
	public static class StartingData
	{
		// The character-create editor playfield/map id (the scene props are WorldObjects rows on it).
		public const uint EditorMapId = 4000;

		// Male (Sex 0): loincloth in the legs slot, no chest item.
		public const uint StartingMaleLegsItem = 3805575; // Red Loincloth

		// Universal: the Slave Manacles worn on the wrist (slot 0x12) by every fresh beach character.
		public const uint StartingWristItem = 3361557; // Slave Manacles

		// The PerformSpecialAction selector that toggles a caster's spellweave, per class. It is not one shared id: the
		// necromancer's activate/deactivate is 0xFC and the demonologist's is 0x10C, and each class uses only its own -
		// across the whole corpus the three NecroL80 spellweave captures carry 0xFC and never 0x10C, while both DemoL80
		// captures carry 0x10C and never 0xFC.
		//
		// They are the same mechanic rather than two unrelated actions because the server's answer is identical: both
		// produce the same player BehaviorControlCenterIIR pair - behaviour 42041 to open the trance, then 41302 to
		// close it - with the same slot 1 and form 4.
		//
		// Priest of Mitra and Tempest of Set also weave (docs/subsystems/combos.md) but no capture shows either doing
		// it, so they are deliberately absent rather than guessed: an unmapped class logs and does nothing, which is
		// visible, where a wrong id would silently start a weave on the wrong input.
		public static readonly ImmutableDictionary<ConanClass, uint> SpellweaveToggleAction = new Dictionary<ConanClass, uint>
		{
			[ConanClass.Necromancer] = 0xFC,
			[ConanClass.Demonologist] = 0x10C
		}.ToImmutableDictionary();

		// The PvP minigame queue capacity per playfield: the total the queue fills before a match forms. The values
		// are the client's own - its minigame window labels every queue "0/24" or "0/12" (Conquest on The Call of
		// Jhebbal Sag at 24; the three Capture The Skull maps and Annihilate Opposing Team on Totem Torrent at 12),
		// so 12v12 and 6v6. Where the client keeps the number is not recovered, so this records what its window
		// displays. Open work tracked in Gitea #146.
		public static readonly ImmutableDictionary<uint, int> MinigameQueueCapacity = new Dictionary<uint, int>
		{
			[5006] = 24, // The Call of Jhebbal Sag - Conquest
			[5201] = 12, // Blood Ravine - Capture The Skull
			[5012] = 12, // Lost Temple - Capture The Skull
			[5005] = 12, // Hallowed Vaults - Capture The Skull
			[5002] = 12 // Totem Torrent - Annihilate Opposing Team
		}.ToImmutableDictionary();

		// A minigame map the table above does not list (a future import) queues at the common size rather than
		// forming degenerately small.
		public const int DefaultMinigameQueueCapacity = 12;

		// The search stance's animation action, per class. Retail answers `PerformSpecialAction` 0x064 with a player
		// `BehaviorControlCenterIIR` at slot `BehaviorId.Search`, and its action id is the character's own - 42013 on
		// the necromancer, 53091 on the Herald of Xotli - while everything else about the record is shared (form 4,
		// flags 8, activation 159, a five-byte zero tail), so the class-varying part is exactly one number.
		//
		// Only the two classes a capture shows are listed, for the same reason SpellweaveToggleAction stops at two: an
		// unmapped class logs and does nothing, which is visible, where a borrowed id would play the wrong animation
		// and read as working. Open work tracked in Gitea #505.
		public static readonly ImmutableDictionary<ConanClass, uint> SearchStanceAction = new Dictionary<ConanClass, uint>
		{
			[ConanClass.Necromancer] = 42013,
			[ConanClass.HeraldOfXotli] = 53091
		}.ToImmutableDictionary();

		// The hide stance's enter animation action, per class - `PerformSpecialAction` 0x068, answered at slot
		// `BehaviorId.SneakHide`. Class-specific like the search stance above: 42014 on the necromancer (adjacent to
		// its search action), 187137 on the Herald of Xotli. Same deliberate two-row limit.
		public static readonly ImmutableDictionary<ConanClass, uint> SneakHideEnterAction = new Dictionary<ConanClass, uint>
		{
			[ConanClass.Necromancer] = 42014,
			[ConanClass.HeraldOfXotli] = 187137
		}.ToImmutableDictionary();

		// Leaving hide is not class-specific: both captured characters exit with the same action id, which is why this
		// is a constant rather than a third map. The enter animation is the character's own sneak pose; the exit is
		// the shared stand-up.
		public const uint SneakHideExitAction = 43818;

		// Each race's home city - where the Totem of Origins sends you. This is a different destination from the bind
		// point: the Path of Asura spell recalls to the bound location (the bindstone/Wayfarer point the player last
		// set), while the Totem returns the player to their race's origin regardless of where they are bound.
		//
		// Confirmed for Stygian from the use_totem_of_origin capture (AssassinL23, rogue-assassin): the player uses the
		// Totem while standing in Old Tarantia at (1001.8, 115.3, 618.8) - ~57 u from Old Tarantia's own respawn point,
		// so a bind recall would have been a no-op - and lands on map 3000 (Khemi) at (759.0, 165.4, 1093.0), 22 u from
		// Khemi's AlwaysKnown "Akhet Docks" point. Khemi is the Stygian city, and the character was neither bound nor
		// standing there. The other three rows are the same rule applied to each race's origin city; only the Stygian
		// one is capture-verified.
		public static readonly ImmutableDictionary<Race, uint> RacialHomeMap = new Dictionary<Race, uint>
		{
			[Race.Cimmerian] = 1000, // Conarch Village
			[Race.Aquilonian] = 2000, // Old Tarantia
			[Race.Stygian] = 3000, // Khemi          (capture-verified)
			[Race.Khitan] = 6400 // Paikang        (Rise of the Godslayer)
		}.ToImmutableDictionary();

		// Which classes each race may create, straight from the client's own RDB 1000507 race x class table (record 0,
		// the shipped set - see docs/rdb/client-data-tables.md).
		//
		// The archetype is not in 1000507 - that table is (race, class) pairs and lists the five archetypes as entries
		// of their own rather than as parents - so the class -> archetype mapping below is ours.
		public static readonly ImmutableArray<(Race race, ArchType archType, ConanClass clas)> RaceArchClassCombinations =
		[
			// Cimmerian - no Mage archetype at all.
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

		// Female (Sex 1): a gown (chest) plus matching bottom (legs). Retail randomises the garment colour per
		// character, so one is picked at random from this slave-gown set (all fully buildable - Hash + Data, Type 101).
		public static readonly ImmutableArray<(uint Gown, uint Bottom)> StartingFemaleOutfits =
		[
			(324066, 324176), // Blue
			(324140, 324168), // Purple
			(324067, 324172), // Red
			(324139, 324171), // Silver
			(105844, 324175) // White
		];

		// Per-class starting weapon (main-hand-left slot 0x06), harvested from the create captures. Coverage is only
		// the classes a create dump shows armed; the rest start unarmed until more create captures land.
		public static readonly ImmutableDictionary<ConanClass, uint> StartingWeapon = new Dictionary<ConanClass, uint>
		{
			[ConanClass.Assassin] = 3735601, // Marlin Spike
			[ConanClass.Necromancer] = 3395744, // Broken Bottle (0x33D0A0; necro create capture, BccIIR slot-6)
			[ConanClass.TempestOfSet] = 215711 // Broken Bottle (0x34A9F - the OTHER bottle; tempest create capture, BccIIR slot-6)
		}.ToImmutableDictionary();

		public static readonly Vector3 EditorSpawnPosition = new Vector3(260.0f, 0.0f, 290.0f);
	}
}
