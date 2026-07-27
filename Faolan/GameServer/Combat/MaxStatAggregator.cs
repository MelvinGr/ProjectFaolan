using System.Collections.Generic;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;

namespace Faolan.GameServer.Combat
{
	// Server-side gear-stat aggregation (#3): sums the stat bonuses a character's EQUIPPED items grant, by stat, so
	// worn gear raises the character's derived stats (the health-bar-fill case: a +HP ring lifts MaxHealth above the
	// class base). Pure over the item rows - the caller supplies the equipped Items (with their StatModifiers loaded),
	// keeping this unit-testable and free of DB/session deps.
	//
	// Each ItemStatModifier.Amount is the RAW WIRE value; this divides by the stat's CharacterStatId.GetScale() to
	// return the DISPLAYED bonus (÷100 vitals + Magic Damage, ÷10 attributes, ÷1 ratings) - the unit a caller adds onto
	// a display MaxHealth / attribute before the wire re-applies the scale.
	public static class MaxStatAggregator
	{
		// Total DISPLAY-unit gear bonus per stat across the given equipped items' StatModifiers. Absent stats = no
		// entry (a caller reads it as 0).
		public static IReadOnlyDictionary<CharacterStatId, int> AggregateGearBonuses(IEnumerable<Item> equippedItems)
		{
			var totals = new Dictionary<CharacterStatId, int>();
			foreach (var item in equippedItems)
			foreach (var mod in item.StatModifiers)
			{
				var scale = (int)mod.StatId.GetScale();
				totals[mod.StatId] = totals.GetValueOrDefault(mod.StatId) + mod.Amount / scale;
			}

			return totals;
		}

		// Total stat bonus per stat across a character's trained FEATS (#251).
		//
		// ⚠️ NO GetScale() division here, unlike the gear path above. FeatEffect.Value is already a DISPLAY value:
		// Strength (scale 10) carries 1..9 in the feat table, which as a raw wire value would be +0.1 Strength, and
		// HealthMax (scale 100) carries 100..700, which would be +1 HP. Dividing these would under-grant every feat
		// by 10x or 100x. The two sources genuinely use different conventions - the item template stores raw wire,
		// the feat table stores display.
		public static IReadOnlyDictionary<CharacterStatId, int> AggregateFeatBonuses(IEnumerable<FeatEffect> effects)
		{
			var totals = new Dictionary<CharacterStatId, int>();
			foreach (var effect in effects)
				totals[effect.StatId] = totals.GetValueOrDefault(effect.StatId) + (int)effect.Value;

			return totals;
		}

		// Sums several bonus sources into one table. The simulation takes a single additive bonus set, and gear and
		// feats are just two contributors to it.
		public static IReadOnlyDictionary<CharacterStatId, int> Combine(params IReadOnlyDictionary<CharacterStatId, int>[] sources)
		{
			var totals = new Dictionary<CharacterStatId, int>();
			foreach (var source in sources)
			foreach (var (stat, amount) in source)
				totals[stat] = totals.GetValueOrDefault(stat) + amount;

			return totals;
		}
	}
}
