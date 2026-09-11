namespace Faolan.Core.Enums
{
	/// <summary>
	///     Why a quest share did (or did not) go through - the <c>i_nFeedbackType</c> word of the two feedback
	///     dispatches DispatchQuestShareGlobalFeedback (0x2048) and DispatchQuestShareIndividualFeedback (0x2049).
	/// </summary>
	/// <remarks>
	///     <para>
	///         The two methods do not carry two enums: they carry two disjoint slices of one 0..11 space, and which
	///         method a code travels on is fixed by the code. Each client handler is a jump table over the type that
	///         resolves an LDB message key and logs "Invalid feedback type (n)" for anything outside its own slice:
	///     </para>
	///     <code>
	///     GamecodeClient_t::QuestShareGlobalFeedback      FUN_01396ef0, table @0x13970bc - accepts 0,1,2,5,6,7
	///     GamecodeClient_t::QuestShareIndividualFeedback  FUN_013970e0, table @0x1397258 - accepts 3,4,8,9,10,11
	///     </code>
	///     <para>
	///         The split is the one the two methods' second field implies: a global code answers the sharer about the
	///         share as a whole and its value is a count (u32), while an individual code names one player and its value
	///         is that player's name (a wire string, not an ID32 - the reader calls the string primitive, FUN_0048f840,
	///         not the id one).
	///     </para>
	///     <para>The names are the client's own LDB message keys, verbatim, minus the shared <c>ShareFeedback</c> prefix.</para>
	///     <para>[V3: AgeOfConan.exe FUN_01396ef0 / FUN_013970e0 jump tables + the message-key operands at 0x207a944..0x207ab18]</para>
	/// </remarks>
	public enum QuestShareFeedback : uint
	{
		// ---- global (0x2048): the answer to my share attempt; Value is a count -------------------------------

		/// <summary>"ShareFeedbackNotInTeam" - you shared while solo. Value unused.</summary>
		NotInTeam = 0,

		/// <summary>"ShareFeedbackMainQuestNotFound" - the shared quest id is not one you hold. Value unused.</summary>
		MainQuestNotFound = 1,

		/// <summary>
		///     "ShareFeedbackMainQuestCanNotShare" - the quest itself forbids sharing: QuestDefinition.QuestLineFlags
		///     bit 4 (0x10), the same bit that greys the journal's Share button out. Value unused.
		/// </summary>
		MainQuestCanNotShare = 2,

		/// <summary>
		///     "ShareFeedbackLegitTargetsOne" / "...Many" - the share went out to Value team-mates. The client picks the
		///     singular key when Value == 1 and the plural (with the number substituted) otherwise, so Value is a count
		///     and 1 is not a flag.
		/// </summary>
		LegitTargets = 5,

		/// <summary>
		///     "ShareFeedbackOutOfRangeTargetsOne" / "...Many" - Value team-mates were too far away to be offered it.
		///     Same singular/plural split as <see cref="LegitTargets" />.
		/// </summary>
		OutOfRangeTargets = 6,

		/// <summary>"ShareFeedbackNoLegitTargets" - nobody in the team could be offered the quest. Value unused.</summary>
		NoLegitTargets = 7,

		// ---- individual (0x2049): about one player; Value is that player's name ------------------------------

		/// <summary>"ShareFeedbackLevelTooLow" - the named player is under the quest's level band.</summary>
		LevelTooLow = 3,

		/// <summary>"ShareFeedbackLevelTooHigh" - the named player is over the quest's level band.</summary>
		LevelTooHigh = 4,

		/// <summary>"ShareFeedbackQuestDeclined" - the named player pressed No on the share window (their c2s ShareQuestDeclined).</summary>
		QuestDeclined = 8,

		/// <summary>"ShareFeedbackPlayerBusy" - the named player's client refused to raise the window (their c2s ShareQuestBusy).</summary>
		PlayerBusy = 9,

		/// <summary>"ShareFeedbackAlreadyFinished" - the named player has already completed the quest.</summary>
		AlreadyFinished = 10,

		/// <summary>"ShareFeedbackDoesNotHaveExpansion" - the named player's account lacks the expansion the quest belongs to.</summary>
		DoesNotHaveExpansion = 11
	}
}
