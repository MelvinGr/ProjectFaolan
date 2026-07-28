namespace Faolan.Core.Enums
{
	// The playable races, ids as the wire and the client's own tables carry them. The set is closed, so the columns
	// that hold it take [ConstrainToEnumValues] - which is exactly why Khitan had to be added rather than left
	// implicit: CHECK (Race IN (1, 2, 3)) would REJECT a Khitan character at the database.
	//
	// Confirmed against RDB 1000507, the client's race x class combination table (docs/rdb/client-data-tables.md):
	// four races, ids 1, 2, 3 and 10, with 10's class set - Herald of Xotli, Demonologist, Necromancer, Bear Shaman,
	// Guardian, Ranger, Assassin, Dark Templar - being the Khitan one from Rise of the Godslayer.
	//
	// ⚠️ Aquilonian and Cimmerian were the other way round until 2026-07-28. A CAPTURED character settles it:
	// Dikketest (Dikketest_rogue-barbarian_create) is female, carries head mesh 1142847 and race byte 1, and that
	// head occurs in exactly ONE appearance list - the female heads under CHARACTERCREATOR's <cimmerian> element.
	// Wire race 1 is Cimmerian, established without reference to this enum.
	//
	// Two readings of RDB 1000507 agree and are what prompted the check. In that table race 1 carries Barbarian and Bear Shaman and NO Mage archetype at all -
	// the Cimmerian profile - while race 2 carries Mage and Priest of Mitra, the Aquilonian one. And matching the
	// class SETS in StartingData.RaceArchClassCombinations against it, ignoring the names entirely, puts what we had
	// labelled Aquilonian at race 2 and Cimmerian at race 1. Race 3 = Stygian was right and is confirmed both ways.
	public enum Race : byte
	{
		Cimmerian = 1,
		Aquilonian = 2,
		Stygian = 3,

		// Rise of the Godslayer. No shipped character uses it yet; the enum carries it so the CHECK admits one.
		Khitan = 10
	}
}
