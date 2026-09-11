using System.Collections.Generic;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Rdb;

namespace Faolan.GameServer.Combat
{
	/// <summary>
	///     Server-side gear-stat aggregation: sums the stat bonuses a character's equipped items grant, by stat, so
	///     worn gear raises the character's derived stats - a +HP ring lifts MaxHealth above the class base.
	/// </summary>
	/// <remarks>
	///     Pure over the item rows: the caller supplies the equipped items with their StatModifiers loaded, which keeps
	///     this free of database and session dependencies. Each <c>ItemStatModifier.Amount</c> is the raw wire value,
	///     so this divides by the stat's <see cref="CharacterStatId" /> scale to return the displayed bonus (/100 for
	///     vitals and Magic Damage, /10 for attributes, /1 for ratings) - the unit a caller adds onto a display
	///     MaxHealth or attribute before the wire re-applies the scale.
	/// </remarks>
	public static class MaxStatAggregator
	{
		/// <summary>
		///     Total display-unit gear bonus per stat across the given equipped items' StatModifiers. An absent stat
		///     has no entry, which a caller reads as 0.
		/// </summary>
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

		// The feat effect kinds whose Value adds to the stat, and so the only ones this sums. The feat region is as
		// heterogeneous as the spell one BuffStatEffects reads, and for the same reason - both are GameAction elements,
		// and the kind is what says how to read the payload:
		//   53045  `Modify`             a flat delta. The bulk of the table.
		//   53220  `InterpolatedModify` an additive delta too, but a level ramp: the row's Value is only the low
		//          53221                endpoint and the whole bonus is `lerp(Value@LowLevel, HighValue@HighLevel)`
		//                               over the carrier's level. Summed through that ramp, so `Battle Roar` is the
		//                               +17500 health it is at 80 rather than the +887 it is at 20.
		// Deliberately not summed, each for a different reason:
		//   53067  `ModifyAsRating`     a percent stored x100, so "+2% Critical Rating" would add +200. It scales a
		//          53068                computed total; BuffStatEffects has the model, this aggregate does not.
		//                               53068 `InterpolatedModifyAsRating` is that same percent as a level ramp -
		//                               its ramp endpoints are percentages, so it is no more summable than 53067,
		//                               which is why it is here and not above. (Zero feat rows carry it today; the
		//                               one shipped row is on a spell.)
		//   53139  `SetFlag`            a flag write, not a bonus - `Basic Riding` and every class feat carry
		//                               (864 Player Flags, 5). Summing two of them invents flag 10.
		//   53026  `Set`                an assignment, not an addition (`Ring of the Griffin` sets 149 Available
		//                               Slots to 65535). Adding it to a base is meaningless.
		private static readonly HashSet<uint> FlatFeatKinds = [53045, 53220, 53221];

		/// <summary>
		///     Total stat bonus per stat across a character's trained feats, counting only the additive kinds above and
		///     interpolating the ramped ones at <paramref name="level" /> - the carrier's level, which is what the
		///     client ramps over (see <see cref="LevelRamp" />).
		/// </summary>
		/// <remarks>
		///     <para>
		///         <c>FeatEffect.Value</c> is a raw wire value, so the scale division is required. Three things say so:
		///         the same bytes are read twice - <c>ClassAttributeBase.Amount</c> and <c>FeatEffect.Value</c> are
		///         both <c>ItemTemplateStatModifiers.Modifier.WireAmount</c> off the same <c>&lt;Class&gt; Class Feat
		///         NNN</c> templates, and <c>Necromancer Class Feat 071</c> carries HealthMax 38,600 exactly as the
		///         <c>ClassAttributeBases</c> row for (Necromancer, 80) does, which <c>CharacterMaxStats.Pool</c> adds
		///         in hundredths for the +386 health verified against every captured pool; the magnitudes are
		///         impossible read as display, since <c>Barbarian: HP (1) 10</c> carries 26,800 against a level-80's
		///         whole pool of about 6,300; and the flat health term the class curves want is in the tens.
		///     </para>
		///     <para>
		///         So feats follow the item convention, which is what the parser they share implies - not the spell
		///         one, where <see cref="BuffStatEffects.SpellScale" /> keeps attributes at scale 1 because spell
		///         records store those in display units. Three sources, three conventions, and the parser says which.
		///     </para>
		/// </remarks>
		public static IReadOnlyDictionary<CharacterStatId, int> AggregateFeatBonuses(IEnumerable<FeatEffect> effects, uint level)
		{
			// Accumulated in wire units and divided once per stat, which is how `CharacterMaxStats.Pool` handles the
			// same numbers: dividing each row first throws away the remainder of every one of them, and a stat built
			// from several feats then reads low by up to one display point per row.
			var wire = new Dictionary<CharacterStatId, int>();
			foreach (var effect in effects)
			{
				if (!FlatFeatKinds.Contains(effect.EffectKind))
					continue;

				// A ramped kind whose three ramp columns are null is a row whose parse could not complete: fall back
				// to the low endpoint, which under-grants rather than reporting a wrong stat.
				var bonus = effect is { HighValue: { } high, LowLevel: { } low, HighLevel: { } top }
					? new LevelRamp(effect.Value, high, low, top).ValueAt(level)
					: effect.Value;

				wire[effect.StatId] = wire.GetValueOrDefault(effect.StatId) + bonus;
			}

			var totals = new Dictionary<CharacterStatId, int>(wire.Count);
			foreach (var (stat, amount) in wire)
				totals[stat] = amount / (int)stat.GetScale();

			return totals;
		}

		/// <summary>
		///     Sums several bonus sources into one table. The simulation takes a single additive bonus set, of which
		///     gear and feats are two contributors.
		/// </summary>
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
