using System.Collections.Generic;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;

namespace Faolan.GameServer.Combat
{
	// Server-side gear-stat aggregation (#3): sums the stat bonuses a character's EQUIPPED items grant, by wire stat
	// id, so worn gear can raise the character's stats. Pure over the item rows - the caller supplies the equipped
	// Items (loaded from the DB), keeping this unit-testable and free of DB/session deps.
	//
	// SCOPE (decode-first): today an item carries only the VALIDATED primary-attribute modifier (Str/Con/Int/Wis/Dex
	// +1/+2/+3, on the Item.*Bonus columns, docs/rdb/item-template-stats.md). The remaining #3 pieces - the
	// secondary-modifier list and the vital/HP bonus (the health-bar-fill case) - are reference-gated and not decoded
	// yet, so they don't appear here yet. When they're decoded + imported (as further Item columns), they flow through
	// this same sum with no change to the wire-stat-id contract.
	//
	// The WIRE APPLICATION (base attribute + this bonus, at the stat block's ×10 attribute scale) is a separate step
	// still gated on a reference tooltip (the exact scale a +N modifier displays as) - this only computes the raw
	// per-stat gear total.
	public static class MaxStatAggregator
	{
		// The primary-attribute bonus columns and the wire stat id each maps onto (the same id space the stat block
		// uses). One place so the column set and its wire ids stay together.
		private static readonly (System.Func<Item, int?> Bonus, uint WireStatId)[] AttributeBonuses =
		[
			(i => i.StrengthBonus, (uint)CharacterStatId.Strength),         // 804
			(i => i.ConstitutionBonus, (uint)CharacterStatId.Constitution), // 806
			(i => i.IntelligenceBonus, (uint)CharacterStatId.Intelligence), // 808
			(i => i.WisdomBonus, (uint)CharacterStatId.Wisdom),             // 810
			(i => i.DexterityBonus, (uint)CharacterStatId.Dexterity)        // 814
		];

		// Total gear bonus per wire stat id across the given equipped items' attribute-bonus columns. Absent stats = no
		// entry (a caller reads it as 0). Amounts are the raw decoded values (RDB field 971), NOT wire-scaled.
		public static IReadOnlyDictionary<uint, int> AggregateGearBonuses(IEnumerable<Item> equippedItems)
		{
			var totals = new Dictionary<uint, int>();
			foreach (var item in equippedItems)
			foreach (var (bonus, wireStatId) in AttributeBonuses)
			{
				if (bonus(item) is int amount)
					totals[wireStatId] = totals.GetValueOrDefault(wireStatId) + amount;
			}

			return totals;
		}
	}
}
