using System;
using System.Collections.Generic;

namespace Faolan.Core.Enums
{
	public enum ConanClass : byte
	{
		PriestOfMitra = 0x18,
		TempestOfSet = 0x1C, // Stormcaller
		BearShaman = 0x1D,
		Conqueror = 0x16,
		DarkTemplar = 0x1F,
		Guardian = 0x14,
		Barbarian = 0x12,
		Ranger = 0x27,
		Assassin = 0x22,
		HeraldOfXotli = 0x2B,
		Demonologist = 0x2C,
		Necromancer = 0x29
	}

	// The class DISPLAY names, and the lookup back from one.
	//
	// These are the client's own spellings, from RDB 1000505 `RDBPlayerClassData_c` - a `[classId][name][tier]`
	// registry whose 12 tier-3 entries are exactly the enum above (`docs/rdb/client-data-tables.md`). They are stated
	// here rather than read from the RDB because the callers that need them (the combo/feat derivations) run in the
	// capture-driven importer modules, which have no `RdbDir`; the registry is the authority the spellings were taken
	// from, and the reason they can be trusted verbatim.
	//
	// ⚠️ This is a NAME lookup, not a parser - it matches the whole string. Anything that wants to find a class named
	// INSIDE a longer string has to split that string itself and decide what a segment is, because "Ranger" is a
	// substring of nothing useful but "Priest" is a substring of "Priest of Mitra".
	public static class ConanClasses
	{
		private static readonly Dictionary<ConanClass, string> Names = new Dictionary<ConanClass, string>
		{
			[ConanClass.Barbarian] = "Barbarian",
			[ConanClass.Guardian] = "Guardian",
			[ConanClass.Conqueror] = "Conqueror",
			[ConanClass.PriestOfMitra] = "Priest of Mitra",
			[ConanClass.TempestOfSet] = "Tempest of Set",
			[ConanClass.BearShaman] = "Bear Shaman",
			[ConanClass.DarkTemplar] = "Dark Templar",
			[ConanClass.Assassin] = "Assassin",
			[ConanClass.Ranger] = "Ranger",
			[ConanClass.Necromancer] = "Necromancer",
			[ConanClass.HeraldOfXotli] = "Herald of Xotli",
			[ConanClass.Demonologist] = "Demonologist"
		};

		private static readonly Dictionary<string, ConanClass> ByName = BuildLookup();

		public static string NameOf(ConanClass @class)
		{
			return Names.GetValueOrDefault(@class, @class.ToString());
		}

		// The class a client display name denotes, or null. Both the spaced form the registry ships
		// ("Bear Shaman", "Herald of Xotli") and the squashed form the asset names use ("BearShaman") resolve, so a
		// caller does not have to know which vocabulary it is holding.
		public static ConanClass? FromName(string? name)
		{
			return name is not null && ByName.TryGetValue(name.Trim(), out var cls) ? cls : null;
		}

		private static Dictionary<string, ConanClass> BuildLookup()
		{
			var map = new Dictionary<string, ConanClass>(StringComparer.OrdinalIgnoreCase);
			foreach (var (cls, name) in Names)
			{
				map[name] = cls;
				map[name.Replace(" ", "")] = cls;
			}

			// The two short forms the combo catalog uses for a class whose registry name is three words.
			map["HoX"] = ConanClass.HeraldOfXotli;
			map["Herald"] = ConanClass.HeraldOfXotli;
			return map;
		}
	}
}
