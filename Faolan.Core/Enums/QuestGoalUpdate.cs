namespace Faolan.Core.Enums
{
	// What a QuestGoalIIR is SAYING about the goal it names. The packet ends in three u32 words that were long
	// modelled as [count][0][0] - a progress counter plus two padding words - which held up only because every
	// single-count goal (TalkToNpc / ReachArea, the overwhelming majority) happens to send (1,0,0).
	//
	// Across the whole capture corpus those three words take exactly THREE combinations in 66/66 packets, so they
	// are not three independent numbers at all: they are one update KIND, spelled as a triple. Notably the first
	// word is not a count - a 3-of-3 collect goal never sends a rising 1,2,3 (see Progress below).
	//
	// The Advanced reading is what pins the set: every quest that emits it has TWO goals, and the packets always
	// run goalA(Satisfied) -> goalA(Advanced) -> goalB(Satisfied). That holds for all four such quests in the
	// corpus - 10238 g1448->g3490, 12369 g7134->g3492, 12371 g3495->g3497, 10287 g400->g7363 - so Advanced is the
	// hand-off marker on the goal being left behind, not a second completion of it.
	//
	// [V2: xxx_mage-necromancer_login_play2_tortage_city_first-quests + Hoxxling_mage-heraldofxotli; 66 QuestGoalIIR
	// packets across 9 captures, 52 Satisfied / 4 Progress / 10 Advanced, no other combination observed]
	public enum QuestGoalUpdate : uint
	{
		// (1,0,0) - the goal is met. Sent for a single-count goal the moment it resolves, and for a multi-count goal
		// on the tick that reaches the required count.
		Satisfied,

		// (2,0,1) - a multi-count goal moved but is NOT met yet. Observed only on quest 12371 goal 3495 ("Get 3 stone
		// bricks"), once per brick for the first two; the third brick sends Satisfied instead. So the wire says
		// "partial" without saying how partial - the running count lives in the journal, not in this packet.
		Progress,

		// (3,2,0) - the goal is closed and the quest moves on to its next goal. Always immediately follows this same
		// goal's Satisfied, and is always followed by the next goal's Satisfied.
		Advanced
	}
}
