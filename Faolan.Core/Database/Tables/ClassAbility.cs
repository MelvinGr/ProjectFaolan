namespace Faolan.Core.Database.Tables
{
	// Which abilities/spells a class has at a given level, imported once from the old C++ server's `const_abilitys`
	// (Archive/). The lists are the archive's id strings (e.g. "769094_1-769099_1"); the client resolves the actual
	// spellbook from class+level, so this is the server-side reference for what to grant. Keyed by (Class, Level).
	public class ClassAbility : BaseEntity
	{
		public string Abilities { get; set; }
		public string AbilitiesNew { get; set; }
		public string Spells { get; set; }
		public string SpellsNew { get; set; }
		public uint Class { get; set; }
		public uint Level { get; set; }
	}
}
