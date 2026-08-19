using System.Collections.Generic;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Rdb;

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

		// The feat effect kinds whose Value adds to the stat, and so the only ones this sums (#597). The feat region
		// is as heterogeneous as the spell one BuffStatEffects reads, and for the same reason - both are GameAction
		// elements, and the KIND is what says how to read the payload:
		//   53045  `Modify`             a flat delta. The bulk of the table.
		//   53220  `InterpolatedModify` an additive delta too, but a LEVEL RAMP: the row's Value is only the low
		//          53221                endpoint and the whole bonus is `lerp(Value@LowLevel, HighValue@HighLevel)`
		//                               over the carrier's level (#599). Summed through that ramp, so `Battle Roar`
		//                               is the +17500 health it is at 80 rather than the +887 it is at 20.
		// Deliberately NOT summed, each for a different reason:
		//   53067  `ModifyAsRating`     a PERCENT stored x100, so "+2% Critical Rating" would add +200. It scales a
		//          53068                computed total; BuffStatEffects has the model, this aggregate does not.
		//                               53068 `InterpolatedModifyAsRating` is that same percent as a level ramp -
		//                               its ramp endpoints are percentages, so it is no more summable than 53067,
		//                               which is why it is here and not above. (Zero feat rows carry it today; the
		//                               one shipped row is on a spell.)
		//   53139  `SetFlag`            a flag WRITE, not a bonus - `Basic Riding` and every class feat carry
		//                               (864 Player Flags, 5). Summing two of them invents flag 10.
		//   53026  `Set`                an assignment, not an addition (`Ring of the Griffin` sets 149 Available
		//                               Slots to 65535). Adding it to a base is meaningless.
		private static readonly HashSet<uint> FlatFeatKinds = [53045, 53220, 53221];

		// Total stat bonus per stat across a character's trained FEATS (#251), counting only the additive kinds above
		// and interpolating the ramped ones at <paramref name="level" /> - the carrier's level, which is what the
		// client ramps over (LevelRamp).
		//
		// ⚠️ NO GetScale() division here, unlike the gear path above. FeatEffect.Value is already a DISPLAY value:
		// Strength (scale 10) carries 1..9 in the feat table, which as a raw wire value would be +0.1 Strength, and
		// HealthMax (scale 100) carries 100..700, which would be +1 HP. Dividing these would under-grant every feat
		// by 10x or 100x. The two sources genuinely use different conventions - the item template stores raw wire,
		// the feat table stores display.
		public static IReadOnlyDictionary<CharacterStatId, int> AggregateFeatBonuses(IEnumerable<FeatEffect> effects, uint level)
		{
			var totals = new Dictionary<CharacterStatId, int>();
			foreach (var effect in effects)
			{
				if (!FlatFeatKinds.Contains(effect.EffectKind))
					continue;

				// A ramped kind whose three ramp columns are null is a row imported before they existed (or one the
				// parse could not complete): fall back to the low endpoint, which is exactly what shipped before -
				// an under-grant, never a wrong stat.
				var bonus = effect is { HighValue: { } high, LowLevel: { } low, HighLevel: { } top }
					? new LevelRamp(effect.Value, high, low, top).ValueAt(level)
					: effect.Value;

				totals[effect.StatId] = totals.GetValueOrDefault(effect.StatId) + bonus;
			}

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
