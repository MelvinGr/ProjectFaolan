using System.ComponentModel.DataAnnotations.Schema;
using Faolan.Core.Database.Attributes;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// The class<->combo access map - the combo analogue of ClassAbility / ClassSpell. Combos are per-class weapon
	// content, so this attributes each combo to the class(es) that actually have it, with a per-class unlock level.
	// Membership is per class and never level-keyed alone: a level-keyed catalog gives every class the same set, so
	// a Necromancer (a caster with no weapon combos) would "learn" Sweep/Slam/Hell Strikes on every login. Casters
	// (Necromancer/Demonologist/Priest of Mitra/Tempest of Set) simply have no rows here, so they get no combos.
	//
	// Compound key (Class, ComboId); FK into the Combos catalog (RDB 1010206). The table ships empty on purpose: the
	// class+level assignments come from dumps later (a scraped combo list carries name+class+level, but the
	// name->ComboId match is lossy, so it is deferred). Empty is the safe default - GrantCharacterCombos then grants
	// nothing rather than the wrong thing ("grant too little, not too much").
	[PrimaryKey(nameof(Class), nameof(ComboId))]
	public class ClassCombo
	{
		// FK into the Combos catalog (Combo.Id = the ComboBookIIR / CharacterCombo combo id).
		public uint ComboId { get; set; }

		[ForeignKey(nameof(ComboId))]
		public virtual Combo Combo { get; set; } = null!;

		[ConstrainToEnumValues]
		public ConanClass Class { get; set; }

		// The character level this combo unlocks at for this class.
		public byte Level { get; set; }
	}
}
