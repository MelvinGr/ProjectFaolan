using System;
using System.Collections.Generic;
using System.Linq;

namespace Faolan.Core.Enums
{
	// The four ARCHETYPES a class belongs to. The ids are the client's own: RDB 1000505 `RDBPlayerClassData_c` is a
	// `[classId][name][tier]` registry, and its TIER-1 entries are exactly these five (`docs/rdb/client-data-tables.md`)
	// - `Commoner` is the pre-class starting state, the other four are the archetypes the create editor picks between
	// before a class.
	public enum ConanArchetype : byte
	{
		Commoner = 0x00,
		Soldier = 0x01,
		Priest = 0x02,
		Rogue = 0x03,
		Mage = 0x04
	}

	// Which classes each archetype holds (#581).
	//
	// The client ships the archetype IDS (1000505 tier 1) but no MEMBERSHIP table - not in the class registry, not in
	// the race x class table (1000507), and not in the CHARACTERCREATOR XML (1010700), which is appearance only. So the
	// membership is taken from the game's own published documentation
	// (https://aoc.fandom.com/wiki/Class, which lists it twice - once as the per-archetype class panels and again as
	// the summary table). It is a stable, public fact about the game rather than a derivation, and it is stated here
	// once so nothing has to re-infer it.
	//
	// ⚠️ It is CORROBORATED by the corpus on one arm, which is why it is trusted rather than merely plausible: the
	// captured Assassin's action bar carries `Rogue_15_dotdetaunt_Vicious_Strike_II_2h` and
	// `Rogue_19_damagedetaunt_Cunning_Strike_VI_2h` - two `Rogue_`-named archetype combos on an Assassin, which is a
	// Rogue. Nothing in the corpus contradicts any of the twelve placements.
	public static class ConanArchetypes
	{
		private static readonly Dictionary<ConanArchetype, ConanClass[]> Members = new()
		{
			[ConanArchetype.Soldier] = [ConanClass.Guardian, ConanClass.DarkTemplar, ConanClass.Conqueror],
			[ConanArchetype.Priest] = [ConanClass.PriestOfMitra, ConanClass.TempestOfSet, ConanClass.BearShaman],
			[ConanArchetype.Rogue] = [ConanClass.Barbarian, ConanClass.Assassin, ConanClass.Ranger],
			[ConanArchetype.Mage] = [ConanClass.Demonologist, ConanClass.HeraldOfXotli, ConanClass.Necromancer]
		};

		private static readonly Dictionary<ConanClass, ConanArchetype> Owner =
			Members.SelectMany(kv => kv.Value.Select(c => (Class: c, Archetype: kv.Key))).ToDictionary(x => x.Class, x => x.Archetype);

		// The three classes of an archetype, or empty for Commoner (which has none - it is the pre-class state).
		public static IReadOnlyList<ConanClass> ClassesOf(ConanArchetype archetype)
		{
			return Members.GetValueOrDefault(archetype, []);
		}

		public static ConanArchetype ArchetypeOf(ConanClass @class)
		{
			return Owner.TryGetValue(@class, out var archetype)
				? archetype
				: ConanArchetype.Commoner;
		}

		// The archetype an ARCHETYPE-SHARED name is for - the `Soldier_` / `Rogue_` / `Priest_` prefix the combo
		// catalog uses. Null for anything else, including a class-named prefix.
		public static ConanArchetype? FromNamePrefix(string name)
		{
			ArgumentNullException.ThrowIfNull(name);
			foreach (var archetype in Members.Keys)
				if (name.StartsWith(archetype + "_", StringComparison.OrdinalIgnoreCase))
					return archetype;

			return null;
		}
	}
}
