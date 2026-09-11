namespace Faolan.Core.Enums
{
	/// <summary>
	///     The <c>i_eYourResult</c> word of DispatchMinigameMatchEnded (GameCharInterface 0x8B) - what the match meant
	///     for the one player it is sent to. The winning side rides the same message's first word and is a
	///     TeamInfo_c::PvPMatchMakingSide_e (the client's own 1/2 side values, which the server only echoes back).
	/// </summary>
	/// <remarks>
	///     <para>
	///         Recovered from the client, not from a capture - no captured session finishes a minigame:
	///     </para>
	///     <list type="bullet">
	///         <item>
	///             the dispatch reader FUN_004cb0b0 takes two u32s off the stream and hands them to the handler
	///             FUN_005465b0, which stores them at PvPMatchMaking_c +0x1d0 (winning side) and +0x1d4 (this result),
	///             sets the status word +0x18c to 4 (ended/results) and zeroes the match clock at +0x1b8;
	///         </item>
	///         <item>
	///             the match-status view copies +0x1d4 onto its own +0x484 (FUN_01737080 @01737f2a) and FUN_01738490
	///             branches on it to pick the LDB-100 header token: 0 -> "MatchWon", 1 -> "MatchLost", anything else ->
	///             "MatchTie" (@0173880c);
	///         </item>
	///         <item>the singleton's reset FUN_005457a0 writes 3 there, the value that means "no result yet".</item>
	///     </list>
	///     <para>[V3: AgeOfConan.exe FUN_004cb0b0 / FUN_005465b0 / FUN_01738490, tokens at 0x20b096c..0x20b0984]</para>
	/// </remarks>
	public enum MinigameResult : uint
	{
		/// <summary>"MatchWon" - the player's side took the match.</summary>
		Won = 0,

		/// <summary>"MatchLost" - the other side took it.</summary>
		Lost = 1,

		/// <summary>
		///     "MatchTie" - neither side won. The client renders this for every value it does not recognise, so it is
		///     also what an out-of-range word degrades to.
		/// </summary>
		Tie = 2,

		/// <summary>What the client's own reset leaves in the field: no match has ended. Never sent.</summary>
		None = 3
	}
}
