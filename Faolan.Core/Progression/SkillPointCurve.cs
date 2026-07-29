using System.Collections.Generic;
using System.Collections.Immutable;

namespace Faolan.Core.Progression
{
	/// <summary>
	///     The skill-point pool (<see cref="Enums.CharacterStatId.SkillPoints" />, stat <c>0x0F3</c>) as a function of
	///     LEVEL - the number the client reports as "gained N skillpoints" (#507).
	/// </summary>
	/// <remarks>
	///     The ticket's question was whether the amount is a function of level before modelling it as an event. It is,
	///     and strictly so: the value is identical for every character at a given level regardless of class or
	///     character. Two different characters of two different classes at level 23 (a necromancer and a
	///     rogue-assassin) both carry 402; two different level-24 characters both carry 642. So this is not an award to
	///     grant on some trigger - it is a level-derived total the login snapshot simply states.
	///     <para>
	///         ⚠️ <b>There is no formula, and these are the only levels measured.</b> The five known points are
	///         0, 154, 402, 642, 1892 at levels 1, 5, 23, 24, 80 - and that is not a smooth curve: level 23 to 24 jumps
	///         240 in a single level while level 5 to 23 averages under 14. So it is a per-level TABLE, and 75 of its 80
	///         rows are simply not known. Interpolating them would be inventing retail data, which is why
	///         <see cref="For" /> returns null for an unmeasured level rather than guessing a value the client would
	///         then display as authoritative.
	///     </para>
	///     <para>
	///         Finding the remaining rows: it is NOT in the client data tables - 1000505-1000508, 1000511, 1000513,
	///         1000514 and 1000516 were dumped and none contains 154, 402, 642 or 1892 in either endianness. The
	///         cheapest source is more captures: one login snapshot per level fills a row each, with no RE at all.
	///     </para>
	/// </remarks>
	public static class SkillPointCurve
	{
		/// <summary>
		///     Level -> pool, read out of the login snapshots' stat sheets. Every entry is a measured retail value; the
		///     table is deliberately sparse rather than filled in.
		/// </summary>
		public static readonly ImmutableSortedDictionary<uint, uint> Measured = new Dictionary<uint, uint>
		{
			[1] = 0,
			[5] = 154,
			[23] = 402,
			[24] = 642,
			[80] = 1892
		}.ToImmutableSortedDictionary();

		/// <summary>
		///     The pool for <paramref name="level" />, or <c>null</c> when that level has never been captured. A null
		///     means "leave whatever the snapshot baseline carried" - for an imported character that is its own real
		///     captured value, which is already correct.
		/// </summary>
		public static uint? For(uint level) => Measured.TryGetValue(level, out var points) ? points : null;
	}
}
