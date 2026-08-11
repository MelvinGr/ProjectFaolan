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
	// ⭐ **The CLIENT states this, in RDB 1000506** (`ClassMembershipTable`, #469) - a `[parentClassId][childClassId]`
	// table whose loader bails with *"Could not load classmembership info. This will cause gui to loose parent class
	// feats"*. Entry 1 is the shipped hierarchy: `Commoner (0)` parents the four archetypes, and each archetype
	// parents its three classes. **All twelve placements below agree with it exactly**; `--report class-hierarchy`
	// re-runs that comparison and fails loudly on any drift.
	//
	// This used to read "the client ships the archetype IDS (1000505 tier 1) but no MEMBERSHIP table", and took the
	// twelve placements from the game's published documentation (https://aoc.fandom.com/wiki/Class) instead. That
	// documentation turned out to be right - and it is now the CORROBORATION rather than the source, alongside the
	// corpus arm that always backed it: the captured Assassin's action bar carries
	// `Rogue_15_dotdetaunt_Vicious_Strike_II_2h` and `Rogue_19_damagedetaunt_Cunning_Strike_VI_2h`, two
	// `Rogue_`-named archetype combos on an Assassin, which is a Rogue.
	//
	// It stays hard-coded rather than read from the RDB because the callers that need it (the combo/feat derivations)
	// run in capture-driven importer modules with no `RdbDir` - the same reason `ConanClasses` inlines the names.
	// The report is what keeps the constant honest.
	//
	// ⚠️ RDB 1000506 entry **0** is a WIDER table - 27 edges, the full authored hierarchy including eleven classes
	// this build never shipped (Soldier alone has six children there). Entry 1 is the one that describes the game.
	public static class ConanArchetypes
	{
		private static readonly Dictionary<ConanArchetype, ConanClass[]> Members = new Dictionary<ConanArchetype, ConanClass[]>
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
