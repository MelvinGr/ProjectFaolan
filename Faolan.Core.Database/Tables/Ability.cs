namespace Faolan.Core.Database.Tables
{
	// An ability/skill template, imported once from the old C++ server's `p_abilities` (Archive/). Same shape as
	// the item identity: Data0..4 is the ability's stable identity quad (used by the cast/grant packets). Names are
	// client-side (resolved by id); the archive provides the few it has.
	public class Ability : BaseEntity
	{
		// True for the universal starting kit granted to EVERY new character during creation (the 7 abilities seen
		// in every create-flow capture: 3 Normal Attack directions + toggle-combat / switch-weapon / utility). The
		// table also catalogs higher-level / class abilities harvested from gameplay captures (IsStartingAbility =
		// false); only the starting set is granted on login (GrantCharacterAbilities). Set by CaptureAbilityImporter.
		public bool IsStartingAbility { get; set; }
		public byte Level { get; set; }
		public string Name { get; set; }

		// Which class this starting ability belongs to (the Class enum value). 0 = GENERIC: granted to every class
		// (the universal kit - Normal Attack L/R/F, toggle-combat, switch-weapon, ...). A non-zero value = the ability
		// is class-specific (it appeared in only one class's create-flow grants). GrantCharacterAbilities grants the
		// generic set plus the character's own class set. Set by CaptureAbilityImporter from the captures.
		public uint Class { get; set; }

		public uint Data0 { get; set; }
		public uint Data1 { get; set; }
		public uint Data2 { get; set; }
		public uint Data3 { get; set; }
		public uint Data4 { get; set; }

		// The ability's wire "slot"/template word: the leading u32 of the 0x2052 ability-grant packet (verified
		// against the strickenbla combat capture: 769094->108, 769099->112). It is the old archive `abilities.slot`
		// column; `p_abilities` does NOT carry it, so the importer backfills it from the old sqlite. Despite the
		// "slot" name it is ability-intrinsic metadata, not a hotbar position.
		public uint Slot { get; set; }
	}
}
