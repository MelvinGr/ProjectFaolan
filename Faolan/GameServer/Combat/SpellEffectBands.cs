using System;
using System.Collections.Generic;

namespace Faolan.GameServer.Combat
{
	// Which of a spell's stat effects apply to a carrier of a given LEVEL (#783).
	//
	// A spell record states one element per level BAND where its numbers change with level: `Hellfire Stream` (8600)
	// carries five `InterpolatedHit` elements on the same stat and period, banded (1-5) (6-10) (11-15) (16-20)
	// (21-30). Every one of them is a real statement about the spell and exactly ONE of them is a statement about
	// this caster. The import used to keep the last and drop the rest, which made a level-1 caster and a level-30
	// caster read the same wrong number; now every band is imported, and something has to choose.
	//
	// ⚠️ Banding is NOT the same as repeating, and the rule below is one rule for both because the DATA distinguishes
	// them. `Life Leech`'s effect record (220980) also carries five elements on one stat - four heals and a drain -
	// but they share ONE band, so every one of them covers the carrier's level and every one of them applies. A rule
	// that picked "the row for this level" per (stat, period) would silently drop three quarters of that heal; a rule
	// that summed everything would grant a level-1 caster all five of Hellfire Stream's bands at once. Selecting by
	// COVERAGE, and summing whatever survives, is what gets both right without asking which shape a record is.
	internal static class SpellEffectBands
	{
		/// <summary>
		///     The members of one (spell, stat, period) group that apply at <paramref name="level" />: the banded ones
		///     whose band covers it - or, when none does, the nearest band - plus every unbanded member, which is
		///     level-independent and always applies.
		/// </summary>
		/// <remarks>
		///     ⚠️ The fallback is a CLAMP, not a drop. A record's bands do not have to reach the carrier's level -
		///     `Hellfire Stream`'s stop at 30 and it is castable well past that - and dropping the effect there would
		///     make a spell do nothing at exactly the levels it is most often cast at. The client's own ramp arithmetic
		///     clamps outside the band for the same reason (<c>LevelRamp.ValueAt</c>); this clamps the CHOICE of band
		///     the same way, so the two agree at the edges.
		/// </remarks>
		public static IReadOnlyList<T> ForLevel<T>(IReadOnlyList<T> group, uint level, Func<T, (uint Low, uint High)?> bandOf)
		{
			if (group.Count <= 1)
				return group;

			// The distance from the level to each band, so "covers it" and "nearest to it" are one comparison: 0 for a
			// band that contains the level, and how far outside it otherwise.
			var best = long.MaxValue;
			foreach (var item in group)
			{
				if (bandOf(item) is not { } band)
					continue;

				best = Math.Min(best, Distance(band, level));
			}

			if (best == long.MaxValue)
				return group; // nothing is banded - this group is plain repeats, and all of it applies

			var applies = new List<T>(group.Count);
			foreach (var item in group)
				if (bandOf(item) is not { } band || Distance(band, level) == best)
					applies.Add(item);

			return applies;
		}

		private static long Distance((uint Low, uint High) band, uint level)
		{
			if (level < band.Low)
				return band.Low - (long)level;

			return level > band.High
				? level - (long)band.High
				: 0;
		}
	}
}
