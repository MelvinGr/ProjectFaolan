using Faolan.Core.Database.Attributes;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// The class/level base a character's stats start from, before gear and buffs. One row per
	// (Class, Level, StatId): the five attributes, plus MaxHealth, plus the caster classes' Non Combat Mana Regen base.
	//
	// Where it comes from. The client computes a character's attributes locally - retail sends a flat 10 for every
	// attribute in the snapshot stat sheet - so this is not on the wire and had to be decoded. It lives in RDB type
	// 1000020 (the item-template type) as a run of plaintext templates, one per class per level, each naming itself
	// "<Class> Class Feat <NNN>", and their stat records are read by the ordinary item parser
	// (ItemTemplateStatModifiers). See docs/subsystems/player-stats.md.
	//
	// Amount is the raw wire value, the same convention as ItemStatModifier - and it is what the client adds to its
	// seeded 10, so a level-1 base of 10 becomes a wire attribute of 20 (display 1). Consumers go through
	// WorldEntity.AttributeScore, which folds base + gear + buffs and hands back display units.
	[PrimaryKey(nameof(Class), nameof(Level), nameof(StatId))]
	public class ClassAttributeBase
	{
		[ConstrainToEnumValues]
		public ConanClass Class { get; set; }

		// 1..80.
		public uint Level { get; set; }

		// The wire stat id: 0x0324..0x032E attributes, 0x0001 MaxHealth, 0x0118 Non Combat Mana Regen.
		//
		// No [ConstrainToEnumValues], and deliberately so. That attribute emits a check against the enum's
		// declared members, and its own remarks name CharacterStatId as the enum not to apply it to: the enum is a
		// catalog of the stats we have named, not of the stat space, and it carries Unknown fillers precisely because
		// wire stat ids are still being decoded. FeatEffect.StatId is the demonstration - the same id space, where
		// feats touch 158 ids and 127 are undeclared, so the check would have rejected four fifths of that table.
		// It was harmless here only by luck (all shipped ids happen to be declared); the first import meeting an
		// unnamed id would have failed as a SQLite constraint violation a long way from the schema decision that
		// caused it, reading as a corrupt capture. Typed as the enum anyway, because the value is one.
		public CharacterStatId StatId { get; set; }

		// The base at wire scale (see class remarks). Signed for consistency with ItemStatModifier; every observed
		// value is positive.
		public int Amount { get; set; }
	}
}
