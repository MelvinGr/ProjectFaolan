using System.ComponentModel.DataAnnotations.Schema;
using Faolan.Core.Database.Attributes;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// The class<->combo ACCESS map - the combo analogue of ClassAbility / ClassSpell. Combos are per-class weapon
	// content, so this attributes each combo to the class(es) that actually have it, with a PER-CLASS unlock LEVEL.
	// It replaces the old class-INDEPENDENT level-keyed catalog, which gave EVERY class the same set - so a
	// Necromancer (a caster with no weapon combos) "learned" Sweep/Slam/Hell Strikes on every login. Casters
	// (Necromancer/Demonologist/Priest of Mitra/Tempest of Set) simply have NO rows here, so they get no combos.
	//
	// Compound key (Class, ComboId); FK into the Combos catalog (RDB 1010206). The table ships EMPTY on purpose: the
	// class+level assignments come from dumps later (the ellatha combos list carries name+class+level, but the
	// name->ComboId match is lossy, so it is deferred). Empty is the safe default - GrantCharacterCombos then grants
	// nothing rather than the wrong thing ("grant too little, not too much").
	[PrimaryKey(nameof(Class), nameof(ComboId))]
	public class ClassCombo
	{
		// FK into the Combos catalog (Combo.Id = the ComboBookIIR / CharacterCombo combo id).
		public uint ComboId { get; set; }

		[ForeignKey(nameof(ComboId))]
		public virtual Combo Combo { get; set; } = null!;

		// The character level this combo unlocks at FOR THIS CLASS.
		public byte Level { get; set; }

		[ConstrainToEnumValues]
		public Class Class { get; set; }
	}
}
