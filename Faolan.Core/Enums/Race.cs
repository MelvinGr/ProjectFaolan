namespace Faolan.Core.Enums
{
	/// <summary>
	///     The playable races, ids as the wire and the client's own tables carry them. The set is closed, so the columns
	///     that hold it take [ConstrainToEnumValues] - which is why Khitan must be listed here: a CHECK over (1, 2, 3)
	///     would reject a Khitan character at the database.
	/// </summary>
	/// <remarks>
	///     <para>
	///         Confirmed against RDB 1000507, the client's race x class combination table (docs/rdb/client-data-tables.md):
	///         four races, ids 1, 2, 3 and 10, with 10's class set - Herald of Xotli, Demonologist, Necromancer, Bear
	///         Shaman, Guardian, Ranger, Assassin, Dark Templar - being the Khitan one from Rise of the Godslayer.
	///     </para>
	///     <para>
	///         Wire race 1 is Cimmerian, settled by a captured character: Dikketest (Dikketest_rogue-barbarian_create)
	///         is female, carries head mesh 1142847 and race byte 1, and that head occurs in exactly one appearance
	///         list - the female heads under CHARACTERCREATOR's <c>&lt;cimmerian&gt;</c> element. RDB 1000507 agrees:
	///         race 1 carries Barbarian and Bear Shaman and no Mage archetype (the Cimmerian profile), race 2 carries
	///         Mage and Priest of Mitra (the Aquilonian one), and matching the class sets in
	///         StartingData.RaceArchClassCombinations against it gives the same assignment. Race 3 = Stygian is
	///         confirmed both ways.
	///     </para>
	/// </remarks>
	public enum Race : byte
	{
		Cimmerian = 1,
		Aquilonian = 2,
		Stygian = 3,

		/// <summary>Rise of the Godslayer. No shipped character uses it yet; the enum carries it so the CHECK admits one.</summary>
		Khitan = 10
	}
}
