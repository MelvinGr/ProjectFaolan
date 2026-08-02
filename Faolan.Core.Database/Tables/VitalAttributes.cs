using System.Collections.Generic;
using Faolan.Core.Enums;

namespace Faolan.Core.Database.Tables
{
	/// <summary>
	///     The five attribute scores a character's health/mana/stamina pools scale off, in DISPLAY units (the sheet's
	///     "Strength 20"), i.e. the class/level base plus everything worn and trained. Fed to
	///     <see cref="CharacterMaxStats.For" />, which turns them into each pool's attribute-scaled term.
	/// </summary>
	public readonly record struct VitalAttributes(uint Strength, uint Constitution, uint Intelligence, uint Wisdom, uint Dexterity)
	{
		/// <summary>
		///     What a level-1 character with no gear carries: <c>ClassAttributeBases</c> holds a flat 10 wire (= 1
		///     display) for all five attributes of all twelve classes at level 1, so this is not a placeholder - it is
		///     that row. It is what reproduces every captured level-1 sheet exactly (Assassin 98 health = the class
		///     base 90 + 8 x this Constitution).
		/// </summary>
		public static VitalAttributes LevelOneBase { get; } = new(1, 1, 1, 1, 1);

		/// <summary>
		///     A character's totals from the two sources the world model carries: the class/level base (#444,
		///     <c>ClassAttributeBases</c>, in the RAW WIRE scale) and the display-unit gear/feat bonuses
		///     (<c>MaxStatAggregator</c>). Absent entries count as zero on both sides - an undecoded (class, level)
		///     row is an honest shortfall the world-entry path already warns about, not a reason to invent a base.
		/// </summary>
		public static VitalAttributes From(IReadOnlyDictionary<CharacterStatId, int>? classBaseWire, IReadOnlyDictionary<CharacterStatId, int>? displayBonuses)
		{
			return new VitalAttributes(Score(CharacterStatId.Strength), Score(CharacterStatId.Constitution), Score(CharacterStatId.Intelligence),
				Score(CharacterStatId.Wisdom), Score(CharacterStatId.Dexterity));

			uint Score(CharacterStatId attribute)
			{
				// ⚠️ The two sources are in DIFFERENT scales: ClassAttributeBase.Amount is raw wire (Constitution 170)
				// and MaxStatAggregator has already divided gear by the stat's scale (Constitution 39). Adding them
				// without this division reads ~10x high.
				var total = (classBaseWire?.GetValueOrDefault(attribute) ?? 0) / (int)attribute.GetScale()
				            + (displayBonuses?.GetValueOrDefault(attribute) ?? 0);

				return total < 0
					? 0u
					: (uint)total;
			}
		}
	}
}
