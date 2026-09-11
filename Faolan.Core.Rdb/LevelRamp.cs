namespace Faolan.Core.Rdb
{
	/// <summary>
	///     The interpolated GameAction family's payload: a stat delta that ramps linearly with the carrier's level,
	///     not a flat bonus. <c>InterpolatedModify</c> (53220), <c>InterpolatedTempChangeSkill</c> (53221) and
	///     <c>InterpolatedModifyAsRating</c> (53068) all carry <c>[0 Stat, 174, 172, 175, 173]</c>, and those four
	///     slots are this: <paramref name="LowValue" /> reached at <paramref name="LowLevel" />,
	///     <paramref name="HighValue" /> at <paramref name="HighLevel" />, clamped outside. <c>Battle Roar</c> is
	///     +887 health at level 20 and +17500 at 80; the decompiled apply is in
	///     <c>docs/re/gameactionlist-grammar.md</c>.
	///     <para>
	///         Which level is not a property of the record. The client reads either stat 150 <c>Casting Level</c> or
	///         stat 965 <c>Apprentice Level</c> off the caster, chosen by a word that is not in the template - see
	///         <see cref="ValueAt" />.
	///     </para>
	/// </summary>
	public readonly record struct LevelRamp(int LowValue, int HighValue, uint LowLevel, uint HighLevel)
	{
		/// <summary>
		///     The delta this ramp grants a carrier of the given level, mirroring the client's own arithmetic
		///     (FUN_008b2320 plus FUN_008c39f0): clamp the level into the ramp, take the fraction, lerp the two values
		///     in float, then truncate. The float and the truncation are the client's, not an approximation - a
		///     double or a rounded divide disagrees with it by one on the values a rank ladder carries.
		///     <para>
		///         A degenerate ramp (<c>HighLevel == LowLevel</c>) takes the fraction 1.0, the high value - which is
		///         the client's own branch, not a guard invented here.
		///     </para>
		///     <para>
		///         The level passed in is the carrier's effective level. The client picks between <c>Casting Level</c>
		///         (150) and <c>Apprentice Level</c> (965) on word 0 of the 9-word invocation context the GameAction
		///         was created with (mode 1 is the cast path, everything else the other) - a runtime property of how
		///         the action was invoked, block-copied into the live object by <c>GameActionBase</c>'s constructor and
		///         absent from the RDB record entirely. Both resolve to the character's level on this server, which
		///         does not model mentoring, so there is nothing to carry here.
		///     </para>
		/// </summary>
		public int ValueAt(uint level)
		{
			var clamped = level < LowLevel
				? LowLevel
				: level > HighLevel
					? HighLevel
					: level;
			var t = HighLevel == LowLevel
				? 1.0f
				: (float)(clamped - LowLevel) / (HighLevel - LowLevel);

			return (int)((HighValue - LowValue) * t + LowValue);
		}
	}
}
