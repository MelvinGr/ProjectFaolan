namespace Faolan.Core.Progression
{
	/// <summary>
	///     Per-level experience curve, anchored on real retail values harvested from the captures.
	/// </summary>
	/// <remarks>
	///     Experience is cumulative on the wire, not within-level. The three XP stats retail sends are a running total
	///     and its two boundaries:
	///     <list type="bullet">
	///         <item><c>Exp</c> (0x034) - the character's running total</item>
	///         <item><c>ExpCurrentLevelBase</c> (0x039) - the running total at which the current level began</item>
	///         <item><c>MaxExp</c> (0x15E) - the running total at which the next level begins</item>
	///     </list>
	///     so the client's XP bar fills <c>(Exp - Base) / (MaxExp - Base)</c> and the cost of a level is the
	///     difference <c>MaxExp - Base</c>. Every captured snapshot agrees, and two adjacent captures prove it
	///     outright: a level-23 character carries base=230240 max=263640, and a level-24 one carries base=263640, the
	///     same number. The level-up event says it a third time: <c>NewLevelIIR</c>'s six "deltas" are
	///     <c>[Exp, Base, MaxExp, 1, 8, 0]</c> (the L2 ding is [100|120|168, 100, 340, 1, 8, 0] across seven captures -
	///     the first varies per player, the next two never do). The anchors below are therefore stored cumulative, as
	///     measured, and the per-level cost is derived.
	///     <para />
	///     The curve is a hand-tuned server table. The client does not hold it: the 25 named <c>CoreTables</c> data
	///     tables contain no XP table, <c>GAMETWEAKS</c> (RDB 1000516) has no level-threshold entry, and the exe's
	///     only XP strings are the three UI value names it fills from these very stats. Captures are the only source,
	///     and levels between two anchors are linearly interpolated on the cumulative curve (monotone, so the derived
	///     per-level costs stay non-decreasing); the anchored levels are exact retail.
	/// </remarks>
	public static class LevelingResolver
	{
		/// <summary>The game's level cap.</summary>
		public const uint MaxLevel = 80;

		/// <summary>
		///     Level to the cumulative experience at which that level begins, harvested from retail. Sources per
		///     anchor:
		///     <list type="bullet">
		///         <item>
		///             L1-L5, L6, L9 - the <c>NewLevelIIR</c> ding events (deltas[1] is the new level's base,
		///             deltas[2] the next level's, so one ding pins two levels) across the create/levelup captures
		///         </item>
		///         <item>L8 - the L8 ding's own base</item>
		///         <item>L23/L24/L25 - the lvl23 and lvl24 account snapshots (base plus MaxExp)</item>
		///         <item>L80/L81 - the max-level logins; 81 is only ever a bar boundary, never reachable</item>
		///     </list>
		///     L7 and everything in 10..22 and 26..79 has no capture and is interpolated.
		/// </summary>
		private static readonly (uint Level, uint Cumulative)[] Anchors =
		[
			(1, 0), (2, 100), (3, 340), (4, 740), (5, 1440), (6, 3940), (8, 10240), (9, 14140),
			(23, 230240), (24, 263640), (25, 298040), (80, 34771240), (81, 47907140)
		];

		/// <summary>
		///     Spends accumulated <paramref name="experience" /> against the curve from <paramref name="level" />:
		///     returns how many levels were gained and the leftover within-level experience. Stops at
		///     <see cref="MaxLevel" />, where the leftover is zeroed rather than carried.
		/// </summary>
		/// <param name="experience">
		///     Within-level progress, which is how the character row stores it; the cumulative form is reconstructed
		///     for the wire as <c>CumulativeXpForLevel(level) + progress</c>.
		/// </param>
		public static (uint LevelsGained, uint RemainingXp) Apply(uint level, uint experience)
		{
			uint gained = 0;
			while (level + gained < MaxLevel)
			{
				var need = XpForNextLevel(level + gained);
				if (experience < need)
					break;

				experience -= need;
				gained++;
			}

			if (level + gained >= MaxLevel)
				experience = 0; // capped: the bar sits empty at max level

			return (gained, experience);
		}

		/// <summary>
		///     The cumulative experience at which <paramref name="level" /> begins - retail's
		///     <c>ExpCurrentLevelBase</c> for a character of that level, and its <c>MaxExp</c> when asked for
		///     level + 1. Exact at a captured anchor, linearly interpolated between the surrounding two otherwise.
		///     Level 1 is 0.
		/// </summary>
		public static uint CumulativeXpForLevel(uint level)
		{
			var l = level == 0
				? 1u
				: level;

			if (l >= Anchors[^1].Level)
				return Anchors[^1].Cumulative;

			for (var i = 0; i < Anchors.Length; i++)
			{
				if (Anchors[i].Level == l)
					return Anchors[i].Cumulative;

				if (Anchors[i].Level > l)
				{
					var (la, ca) = Anchors[i - 1];
					var (lb, cb) = Anchors[i];
					return ca + (uint)((ulong)(cb - ca) * (l - la) / (lb - la));
				}
			}

			return Anchors[^1].Cumulative;
		}

		/// <summary>
		///     Experience required to advance from <paramref name="level" /> to level + 1 - the difference between two
		///     cumulative thresholds, which is what retail's own bar measures
		///     (<c>MaxExp - ExpCurrentLevelBase</c>). Exact wherever both ends are anchored (L1-L5:
		///     100/240/400/700/2500; L23: 33,400; L24: 34,400).
		/// </summary>
		public static uint XpForNextLevel(uint level)
		{
			var l = level == 0
				? 1u
				: level;

			return CumulativeXpForLevel(l + 1) - CumulativeXpForLevel(l);
		}
	}
}
