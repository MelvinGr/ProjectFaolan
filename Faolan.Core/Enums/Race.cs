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
	// ⚠️ 1 and 2 may be SWAPPED here. In 1000507 race 1 has Barbarian and Bear Shaman and no Mage archetype at all
	// (the Cimmerian profile) while race 2 has Mage and Priest of Mitra (the Aquilonian one) - the opposite of the
	// names below. Race 3 = Stygian IS confirmed both ways (the table's class set, and all four shipped characters
	// are race 3 carrying classes it allows). We hold no race-1 or race-2 character, so nothing in our own data
	// settles it; left as-is deliberately rather than flipped on a reading of class sets alone.
	public enum Race : byte
	{
		Aquilonian = 1,
		Cimmerian = 2,
		Stygian = 3,

		// Rise of the Godslayer. No shipped character uses it yet; the enum carries it so the CHECK admits one.
		Khitan = 10
	}
}
