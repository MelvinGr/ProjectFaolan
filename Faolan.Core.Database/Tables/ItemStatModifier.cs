using System.ComponentModel.DataAnnotations.Schema;
using Faolan.Core.Database.Attributes;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One stat bonus an item grants when equipped: a (wire stat id, amount) pair. Rows are the whole tooltip stat
	// list - attributes (Str/Con/Int/Wis/Dex), vitals (HealthMax/StaminaMax/ManaMax) and ratings (Magic Damage, Hit
	// Rating, ...). Decoded from the RDB item-template stat-modifier region + the field-972 primary attribute
	// (RdbItemImporter -> ItemTemplateStatModifiers, docs/rdb/item-template-stats.md, #3).
	//
	// KEY is the natural (ItemId, StatId): an item never carries a stat id twice, so this needs no surrogate id and it
	// enforces the no-duplicate invariant + makes re-import a clean per-(item,stat) upsert.
	//
	// Amount is the RAW WIRE value (as it sits in the template): divide by CharacterStatId.GetScale() for the displayed
	// bonus (÷100 vitals + Magic Damage, ÷10 attributes, ÷1 ratings). Storing raw keeps the row a faithful mirror of
	// the wire; the scale is applied by the consumer (MaxStatAggregator).
	[PrimaryKey(nameof(ItemId), nameof(StatId))]
	public class ItemStatModifier
	{
		public uint ItemId { get; set; }

		[ForeignKey(nameof(ItemId))]
		public virtual Item Item { get; set; } = null!;

		// The wire stat id: 0x0001 HealthMax, 0x01F9 StaminaMax, 0x01FB ManaMax, 0x0324..0x032E attributes, 0x0136 Hit Rating, 0x035D Magic Damage
		//
		// ⚠️ NO [ConstrainToEnumValues], and deliberately so (#452). That attribute emits a CHECK against the enum's
		// DECLARED members, and its own remarks name CharacterStatId as the enum NOT to apply it to: the enum is a
		// catalog of the stats we have NAMED, not of the stat space, and it carries Unknown fillers precisely because
		// wire stat ids are still being decoded. FeatEffect.StatId is the demonstration - the SAME id space, where
		// feats touch 158 ids and 127 are undeclared, so the CHECK would have rejected four fifths of that table.
		// It was harmless here only by luck (all shipped ids happen to be declared); the first import meeting an
		// unnamed id would have failed as a SQLite constraint violation a long way from the schema decision that
		// caused it, reading as a corrupt capture. Typed as the enum anyway, because the value IS one.
		public CharacterStatId StatId { get; set; }

		// The bonus at wire scale (see class remarks). Signed: a debuff-style modifier could be negative, though gear
		// bonuses seen so far are all positive.
		public int Amount { get; set; }
	}
}
