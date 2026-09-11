using System.ComponentModel.DataAnnotations.Schema;
using Faolan.Core.Database.Attributes;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// The class<->ability access map - the ability analogue of ClassSpell, but a true many-to-many: one row per class
	// that has the ability, not collapsed to a single "generic" class. An ability is genuinely multi-class (Spellweaving
	// = every caster, Pet commands = Necro+Demo, the Normal Attacks = everyone), and our caster-heavy captures would
	// make a "2+ classes => generic" collapse wrongly grant e.g. Spellweaving to a fighter. The unlock level lives here
	// (per-class - an ability can unlock at a different level for different classes), not on the ability. Compound key
	// (Class, AbilityId); derived from the per-character grants (CharacterAbility x Character) by CaptureAbilityImporter,
	// so coverage is the classes we have captures for (create-flow + the captured chars).
	[PrimaryKey(nameof(Class), nameof(AbilityId))]
	public class ClassAbility
	{
		// FK into the Abilities catalog (Ability.Id). The catalog row always exists (harvested from the 0x2052 grants).
		public uint AbilityId { get; set; }

		[ForeignKey(nameof(AbilityId))]
		public virtual Ability Ability { get; set; } = null!;

		[ConstrainToEnumValues]
		public ConanClass Class { get; set; }

		// The character level this ability unlocks at for this class (min level of that class's captured holders).
		public byte Level { get; set; }
	}
}
