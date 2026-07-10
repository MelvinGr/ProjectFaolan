using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// A stat bonus an item TEMPLATE grants (one row per item+stat). Decoded from the encrypted RDB item-template
	// property table (RdbItemImporter; docs/rdb/item-template-stats.md): the VALIDATED primary-attribute modifier is
	// field 972 = the wire stat id (804=Str / 806=Con / 808=Int / 810=Wis / 814=Dex) and field 971 = the amount
	// (+1/+2/+3), clean across all 1964 templates (972 holds ONLY those 5 ids, never a non-attribute value). This is
	// the (statId, amount) foundation for #3's server-side MAX-STAT aggregation (MaxStatAggregator). The secondary
	// modifier list + the vital/HP bonus are NOT decoded yet (they need a reference tooltip - docs), so only the
	// primary-attribute modifier is imported here; the table is keyed to take those additional stats when unblocked.
	[PrimaryKey(nameof(ItemId), nameof(WireStatId))]
	public class ItemStat
	{
		public uint ItemId { get; set; }

		[ForeignKey(nameof(ItemId))]
		public virtual Item Item { get; set; }

		// The modifier's WIRE stat id (the same id space the stat block uses): 804=Str / 806=Con / 808=Int / 810=Wis
		// / 814=Dex for the primary-attribute modifier. Stored raw so a future secondary/vital stat slots in unchanged.
		public uint WireStatId { get; set; }

		// The bonus amount as it sits in the template (RDB field 971 = +1/+2/+3 for the primary attribute). Stored as
		// the raw decoded amount; the WIRE SCALE to apply it at (attributes are ×10 in the stat block) is a separate,
		// reference-gated step in the aggregator/wire, NOT baked in here.
		public int Amount { get; set; }
	}
}
