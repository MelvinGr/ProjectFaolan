using System.Collections.Generic;
using Faolan.Core.Database.Tables;

namespace Faolan.GameServer.Combat
{
	// Server-side gear-stat aggregation (#3): sums the stat bonuses a character's EQUIPPED items grant, by wire stat
	// id, so worn gear can raise the character's stats. Pure over the item rows - the caller supplies the equipped
	// items' ItemStat rows (loaded from the DB), keeping this unit-testable and free of DB/session deps.
	//
	// SCOPE (decode-first): today ItemStat only carries the VALIDATED primary-attribute modifier (Str/Con/Int/Wis/Dex
	// +1/+2/+3, docs/rdb/item-template-stats.md). The remaining #3 pieces - the secondary-modifier list and the
	// vital/HP bonus (the health-bar-fill case) - are reference-gated and not yet in ItemStat, so they don't appear
	// here yet. When they're decoded + imported, they flow through this same sum with no change.
	//
	// The WIRE APPLICATION (base attribute + this bonus, at the stat block's ×10 attribute scale) is a separate step
	// still gated on a reference tooltip (the exact scale a +N modifier displays as) - this only computes the raw
	// per-stat gear total.
	public static class MaxStatAggregator
	{
		// Total gear bonus per wire stat id across the given equipped items' ItemStat rows. Absent stats = no entry
		// (a caller reads it as 0). Amounts are the raw decoded values (RDB field 971), NOT wire-scaled.
		public static IReadOnlyDictionary<uint, int> AggregateGearBonuses(IEnumerable<ItemStat> equippedStats)
		{
			var totals = new Dictionary<uint, int>();
			foreach (var s in equippedStats)
				totals[s.WireStatId] = totals.GetValueOrDefault(s.WireStatId) + s.Amount;

			return totals;
		}
	}
}
