using System.ComponentModel.DataAnnotations;
namespace Faolan.Core.Database.Tables
{
	// An ability/skill catalog entry (keyed by its RDB id). Data0..4 is the ability's stable identity quad from the
	// 0x2052 grant - also the TEA key for its RDB 1000020 template (the source of Name, via RdbAbilityImporter). WHICH
	// classes have the ability, and at WHAT level, live in ClassAbility (the class<->ability access map) - a true
	// multi-class membership derived from the per-character grants - NOT here (the ability template carries no
	// class/level requirement). This replaces the old IsStartingAbility flag + single Class/Level columns.
	public class Ability : BaseEntity
	{
		[MaxLength(128)]
		public string Name { get; set; }

		public uint Data0 { get; set; }
		public uint Data1 { get; set; }
		public uint Data2 { get; set; }
		public uint Data3 { get; set; }
		public uint Data4 { get; set; }

		// The ability's wire "slot"/template word: the leading u32 of the 0x2052 ability-grant packet (verified against
		// the strickenbla combat capture: 769094->108, 769099->112). Despite the "slot" name it is ability-intrinsic
		// metadata, not a hotbar position.
		public uint Slot { get; set; }
	}
}
