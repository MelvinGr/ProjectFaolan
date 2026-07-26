using Faolan.Core.Database.Attributes;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// The class/level BASE a character's stats start from, before gear and buffs (#444). One row per
	// (Class, Level, StatId): the five attributes, plus MaxHealth, plus the caster classes' Non Combat Mana Regen base.
	//
	// WHERE IT COMES FROM. The client computes a character's attributes locally - retail sends a flat 10 for every
	// attribute in the snapshot stat sheet - so this is not on the wire and had to be decoded. It lives in RDB type
	// 1000020 (the ITEM-template type) as a run of PLAINTEXT templates, one per class per level, each naming itself
	// "<Class> Class Feat <NNN>", and their stat records are read by the ordinary item parser
	// (ItemTemplateStatModifiers). See docs/subsystems/player-stats.md.
	//
	// Amount is the RAW WIRE value, the same convention as ItemStatModifier - and it is what the client ADDS to its
	// seeded 10, so a level-1 base of 10 becomes a wire attribute of 20 (display 1). Consumers go through
	// WorldEntity.AttributeScore, which folds base + gear + buffs and hands back DISPLAY units.
	[PrimaryKey(nameof(Class), nameof(Level), nameof(StatId))]
	public class ClassAttributeBase
	{
		[ConstrainToEnumValues]
		public Class Class { get; set; }

		// 1..80.
		public uint Level { get; set; }

		// The wire stat id: 0x0324..0x032E attributes, 0x0001 MaxHealth, 0x0118 Non Combat Mana Regen.
		[ConstrainToEnumValues]
		public CharacterStatId StatId { get; set; }

		// The base at wire scale (see class remarks). Signed for consistency with ItemStatModifier; every observed
		// value is positive.
		public int Amount { get; set; }
	}
}
