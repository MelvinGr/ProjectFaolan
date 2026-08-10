using Faolan.Core.Enums;

namespace Faolan.Core.Progression
{
	/// <summary>
	///     The level-scaled rating QUAD - <see cref="CharacterStatId.AttackRating" /> (0x398),
	///     <see cref="CharacterStatId.DefenseRating" /> (0x39A), <see cref="CharacterStatId.MagicRating" /> (0x39B) and
	///     <see cref="CharacterStatId.MagicalResist" /> (0x39D) - as a function of LEVEL (#685).
	/// </summary>
	/// <remarks>
	///     <para>
	///         Unlike <see cref="SkillPointCurve" /> next door, this one IS a formula, and an exact one:
	///         <c>400 x level - 300</c>. Level 1 is 100 and every level adds 400, so level 80 is 31,700.
	///     </para>
	///     <para>
	///         Measured over the whole corpus: 2,100 captured <c>StatIIR</c> records carrying one of the four ids, in
	///         31 captures, for 13 different characters of 6 classes, taking 8 distinct values - 100, 500, 900, 1300,
	///         1700, 8900, 9300, 31700, i.e. levels 1-5, 23, 24 and 80. <b>Every one of the 2,100 fits, with zero
	///         exceptions</b>, and the per-character progressions are monotonic and match what each capture was doing
	///         (three beach captures walk a character 1 -> 5 a level at a time). All four ids always carry the SAME
	///         value in the same send, and it is class-independent - a soldier-guardian, a rogue-assassin, a
	///         necromancer, a demonologist and a Herald of Xotli all sit on the same line.
	///     </para>
	///     <para>
	///         ⚠️ This is the BASE, the same way <c>ClassAttributeBase</c> is: it is what the quad reads with nothing
	///         else applied. Gear and buffs plainly move real combat ratings in retail, and no capture here isolates
	///         one - every character measured was wearing what it was wearing. So a future gear/buff contribution adds
	///         ON TOP of this rather than replacing it.
	///     </para>
	///     <para>
	///         ⚠️ It is NOT a spellweave record. The four ids were modelled as opaque "fire stages" of the necromancer
	///         weave's deactivate burst, with the level-80 caster's own 31,700 mistaken for a burst constant. 20 of the
	///         31 captures carrying these records contain no spellweaving at all - including a Herald of Xotli, which
	///         cannot weave - so the weave was simply where one capture happened to hold them.
	///     </para>
	///     <para>
	///         <b>Two of the occasions retail re-sends it on are now measured</b>, and the server sends it on both
	///         (#505/#685): a <b>LEVEL-UP</b> (15 of the 18 captured <c>NewLevelIIR</c> events draw a burst, 11 of them
	///         within 0.1 s - inside the ding burst itself), and the <b>COMBAT TOGGLE</b> (13 of 13 captured
	///         <c>PerformSpecialAction</c> 0x4E2 presses draw one 0.31-1.61 s later). The toggle is causal rather than
	///         adjacent: the corpus-wide median gap between bursts is 10.4 s, so 13 for 13 inside a 2 s window is not
	///         coincidence - and mechanically a sheathe/draw changes the active weapon, so the ratings are restated.
	///         The four ids always go out in the <see cref="Stats" /> order below.
	///     </para>
	///     <para>
	///         [V0: what ELSE makes retail re-send the quad. Bursts arrive every ~10 s in a live capture and most
	///         follow neither occasion above, mostly alone in their own packets, so it is event-driven rather than a
	///         timer - a stat-sheet recompute is the obvious candidate. The VALUE is settled; the full cadence is not.]
	///     </para>
	/// </remarks>
	public static class CombatRatingCurve
	{
		/// <summary>The four stat ids that carry this curve. All four take the same value at a given level.</summary>
		public static readonly CharacterStatId[] Stats =
		[
			CharacterStatId.AttackRating, CharacterStatId.DefenseRating, CharacterStatId.MagicRating, CharacterStatId.MagicalResist
		];

		/// <summary>
		///     The quad's value at <paramref name="level" />. Level 0 is treated as level 1 - a character below level 1
		///     does not exist, and returning -300 would underflow the unsigned wire field.
		/// </summary>
		public static uint For(uint level)
		{
			return 400 * (level < 1 ? 1 : level) - 300;
		}
	}
}
