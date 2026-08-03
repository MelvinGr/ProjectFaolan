namespace Faolan.Core.Rdb
{
	/// <summary>
	///     The INTERPOLATED GameAction family's payload: a stat delta that ramps LINEARLY with the carrier's level,
	///     not a flat bonus. `InterpolatedModify` (53220), `InterpolatedTempChangeSkill` (53221) and
	///     `InterpolatedModifyAsRating` (53068) all carry `[0 Stat, 174, 172, 175, 173]`, and those four slots are
	///     this: <paramref name="LowValue" /> reached at <paramref name="LowLevel" />, <paramref name="HighValue" />
	///     at <paramref name="HighLevel" />, clamped outside. `Battle Roar` is +887 health at level 20 and +17500 at
	///     80 (Gitea #597/#599; the decompiled apply is in docs/re/gameactionlist-grammar.md).
	///     <para>
	///         ⚠️ Which LEVEL is not a property of the record. The client reads either stat 150 `Casting Level` or
	///         stat 965 `Apprentice Level` off the caster, chosen by a word that is NOT in the template - see
	///         <see cref="ValueAt" />.
	///     </para>
	/// </summary>
	public readonly record struct LevelRamp(int LowValue, int HighValue, uint LowLevel, uint HighLevel)
	{
		/// <summary>
		///     The delta this ramp grants a carrier of the given level, mirroring the client's own arithmetic
		///     (`FUN_008b2320` + `FUN_008c39f0`): clamp the level into the ramp, take the fraction, lerp the two
		///     values in FLOAT, then truncate. The float and the truncation are the client's, not an approximation -
		///     a double or a rounded divide disagrees with it by one on the values a rank ladder actually carries.
		///     <para>
		///         A degenerate ramp (<c>HighLevel == LowLevel</c>) takes the fraction 1.0, i.e. the HIGH value -
		///         which is the client's own branch, not a guard invented here.
		///     </para>
		///     <para>
		///         The level passed in is the carrier's effective level. The client picks between `Casting Level`
		///         (150) and `Apprentice Level` (965) on word 0 of the 9-word invocation context the GameAction was
		///         created with (mode 1 = the cast path, everything else the other) - a RUNTIME property of how the
		///         action was invoked, block-copied into the live object by `GameActionBase`'s constructor and absent
		///         from the RDB record entirely. Both resolve to the character's level on this server, which does not
		///         model mentoring, so there is nothing to carry here.
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
