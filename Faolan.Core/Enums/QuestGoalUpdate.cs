namespace Faolan.Core.Enums
{
	/// <summary>
	///     What a QuestGoalIIR is saying about the goal it names. The packet ends in three u32 words that are not three
	///     independent numbers but one update kind spelled as a triple: across the whole capture corpus they take
	///     exactly three combinations in 66/66 packets. The first word is not a count - a 3-of-3 collect goal never
	///     sends a rising 1,2,3 (see <see cref="Progress" />).
	/// </summary>
	/// <remarks>
	///     <para>
	///         The Advanced reading pins the set: every quest that emits it has two goals, and the packets always run
	///         goalA(Satisfied) -> goalA(Advanced) -> goalB(Satisfied). That holds for all four such quests in the
	///         corpus - 10238 g1448->g3490, 12369 g7134->g3492, 12371 g3495->g3497, 10287 g400->g7363 - so Advanced is
	///         the hand-off marker on the goal being left behind, not a second completion of it.
	///     </para>
	///     <para>
	///         [V2: xxx_mage-necromancer_login_play2_tortage_city_first-quests + Hoxxling_mage-heraldofxotli; 66
	///         QuestGoalIIR packets across 9 captures, 52 Satisfied / 4 Progress / 10 Advanced, no other combination
	///         observed]
	///     </para>
	/// </remarks>
	public enum QuestGoalUpdate : uint
	{
		/// <summary>
		///     (1,0,0) - the goal is met. Sent for a single-count goal the moment it resolves, and for a multi-count
		///     goal on the tick that reaches the required count.
		/// </summary>
		Satisfied,

		/// <summary>
		///     (2,0,1) - a multi-count goal moved but is not met yet. Observed only on quest 12371 goal 3495 ("Get 3
		///     stone bricks"), once per brick for the first two; the third brick sends Satisfied instead. The wire says
		///     "partial" without saying how partial - the running count lives in the journal, not in this packet.
		/// </summary>
		Progress,

		/// <summary>
		///     (3,2,0) - the goal is closed and the quest moves on to its next goal. Always immediately follows this
		///     same goal's Satisfied, and is always followed by the next goal's Satisfied.
		/// </summary>
		Advanced
	}
}
