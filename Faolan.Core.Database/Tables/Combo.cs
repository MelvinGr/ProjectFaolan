using System.ComponentModel.DataAnnotations;

namespace Faolan.Core.Database.Tables
{
	// A combo definition catalog entry, imported from the client RDB type 1010206 ("Combo animation defs") - which,
	// unlike the TEA-encrypted ability/item templates, is PLAINTEXT XML, so no key is needed. Each <combo> element
	// carries the combo's name, its unlock level, the feat it belongs to (featid -> the class feat tree), timing
	// (cooldown / stamina), the weapon criteria, and the input action sequence. Keyed by the combo id (the same id
	// ComboBookIIR grants and CharacterCombo stores). The catalog counterpart of Spell / Ability.
	public class Combo : BaseEntity
	{
		// The feat this combo belongs to (XML `featid`) - links into the class feat tree (1000020), i.e. the class it
		// is for. 0 when the XML omits it.
		public uint FeatId { get; set; }
		public uint GameActionId { get; set; } // XML `gameactionid`

		public uint ParentId { get; set; } // combo-tree parent (XML `parentid`)

		// The character level the combo unlocks at (XML `level`). This IS present here (unlike the ability templates).
		public byte Level { get; set; }
		public int Cooldown { get; set; } // seconds (XML `cooldown`)
		public int Criteria { get; set; } // weapon/state criteria code (XML `criteria`)
		public int Quality { get; set; } // XML `quality`
		public int Stamina { get; set; } // XML `stamina`

		// The input action sequence that performs the combo, comma-separated (e.g. "CombatUpperRight,CombatUpperLeft"),
		// from the child <action name="..."/> elements in order.
		[MaxLength(128)]
		public string Actions { get; set; } = null!;

		[MaxLength(128)]
		public string Animation { get; set; } = null!; // XML `animation`
		// The combo id (Id, from BaseEntity) is the RDB 1010206 entry id = the ComboBookIIR/CharacterCombo combo id.

		[MaxLength(128)]
		public string Name { get; set; } = null!;
	}
}
