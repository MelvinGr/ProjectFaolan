namespace Faolan.Core.Enums
{
	// The `i_eYourResult` word of DispatchMinigameMatchEnded (GameCharInterface 0x8B) - what the match meant for the
	// ONE player it is sent to. The winning side rides the same message's first word and is a
	// TeamInfo_c::PvPMatchMakingSide_e (the client's own 1/2 side values, which the server only ever echoes back).
	//
	// Recovered from the client, not from a capture - no captured session ever finishes a minigame:
	//
	//   * the dispatch reader FUN_004cb0b0 takes two u32s off the stream and hands them to the handler
	//     FUN_005465b0, which stores them at PvPMatchMaking_c +0x1d0 (winning side) and +0x1d4 (this result),
	//     sets the status word +0x18c to 4 (= ended/results) and zeroes the match clock at +0x1b8;
	//   * the match-status view copies +0x1d4 onto its own +0x484 (FUN_01737080 @01737f2a) and FUN_01738490
	//     branches on it to pick the LDB-100 header token: 0 -> "MatchWon", 1 -> "MatchLost", anything else ->
	//     "MatchTie" (@0173880c: TEST EAX,EAX / CMP EAX,0x1);
	//   * the singleton's reset FUN_005457a0 writes 3 there, which is the value that means "no result yet".
	//
	// [V3: AgeOfConan.exe FUN_004cb0b0 / FUN_005465b0 / FUN_01738490, tokens at 0x20b096c..0x20b0984]
	public enum MinigameResult : uint
	{
		// "MatchWon" - the player's side took the match.
		Won = 0,

		// "MatchLost" - the other side took it.
		Lost = 1,

		// "MatchTie" - neither side won. The client renders this for every value it does not recognise, so it is
		// also what an out-of-range word degrades to.
		Tie = 2,

		// What the client's own reset leaves in the field: no match has ended. Never sent.
		None = 3
	}
}
