using System.ComponentModel.DataAnnotations.Schema;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One stat entry a spell carries in its RDB template's [2][9] region - a (kind, wire stat id, amount) record,
	// decoded by the SAME parser as item templates (RdbSpellImporter -> ItemTemplateStatModifiers,
	// docs/rdb/item-template-stats.md, #367). 866 spells carry it (1714 rows).
	//
	// ⚠️ The region is HETEROGENEOUS - NOT a clean "buff = +N stat" list - and EffectKind is what resolves that: the
	// word opening each record is an effect-kind tag, and it says how to read Amount (flat vs percent vs a health
	// effect). See docs/rdb/spell-stat-effects.md for the classification and its evidence. Consequences worth knowing
	// before consuming this table:
	//   - a PERCENT record (kind 53067) stores percent ×100 - Philtre of Guile 0x138 = 200 is "+2% Critical Rating",
	//     not +200;
	//   - a health record (kind 53219/53002/…) is an instant heal or the -100000000 drain/kill sentinel, not a buff;
	//   - attributes on a FLAT record store DISPLAY units (Dexterity Buff 1/2 = 0x32E +1/+2, confirmed by the client's
	//     own description strings), while items store the same attribute ×10 - so ItemStatModifier's GetScale does not
	//     transfer to spell attributes.
	// The server therefore applies only the (kind, stat) pairs it can justify - see BuffStatEffects, which honors flat
	// records on the stats whose wire scale we verified against in-client tooltips, and logs the rest.
	//
	// KEY is the natural (SpellId, StatId): a spell never lists a stat twice (last wire write wins on a repeat).
	// Amount is the RAW template value at whatever scale that stat id / kind uses.
	[PrimaryKey(nameof(SpellId), nameof(StatId))]
	public class SpellStatEffect
	{
		public uint SpellId { get; set; }

		[ForeignKey(nameof(SpellId))]
		public virtual Spell Spell { get; set; } = null!;

		public CharacterStatId StatId { get; set; }

		// The record's effect-KIND tag, verbatim from the template (the ~53000-range word that opens the record).
		// 0 for a record that starts the body, where the tag would sit before it - unreadable, so never honored.
		public uint EffectKind { get; set; }

		public int Amount { get; set; }
	}
}
