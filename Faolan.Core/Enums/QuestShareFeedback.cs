namespace Faolan.Core.Enums
{
	// Why a quest share did (or did not) go through - the `i_nFeedbackType` word of the two feedback dispatches
	// DispatchQuestShareGlobalFeedback (0x2048) and DispatchQuestShareIndividualFeedback (0x2049).
	//
	// The two methods do NOT carry two enums: they carry two disjoint slices of ONE 0..11 space, and which method a
	// code travels on is fixed by the code. The client proves both halves - each handler is a jump table over the
	// type that resolves an LDB message key and logs "Invalid feedback type (n)" for anything outside its own slice:
	//
	//   GamecodeClient_t::QuestShareGlobalFeedback      FUN_01396ef0, table @0x13970bc - accepts 0,1,2,5,6,7
	//   GamecodeClient_t::QuestShareIndividualFeedback  FUN_013970e0, table @0x1397258 - accepts 3,4,8,9,10,11
	//
	// The split is exactly the one the two methods' SECOND field implies: a GLOBAL code answers the sharer about the
	// share as a whole and its value is a COUNT (u32), while an INDIVIDUAL code names one player and its value is
	// that player's NAME (a wire string, not the ID32 the `i_c` prefix might suggest - the reader calls the
	// string primitive, FUN_0048f840, not the id one).
	//
	// The names are the client's own LDB message keys, verbatim, minus the shared `ShareFeedback` prefix.
	//
	// [V3: AgeOfConan.exe FUN_01396ef0 / FUN_013970e0 jump tables + the message-key operands at 0x207a944..0x207ab18]
	public enum QuestShareFeedback : uint
	{
		// ---- global (0x2048): the answer to MY share attempt; Value is a count -------------------------------

		// "ShareFeedbackNotInTeam" - you shared while solo. Value unused.
		NotInTeam = 0,

		// "ShareFeedbackMainQuestNotFound" - the shared quest id is not one you hold. Value unused.
		MainQuestNotFound = 1,

		// "ShareFeedbackMainQuestCanNotShare" - the quest itself forbids sharing: QuestDefinition.QuestLineFlags
		// bit 4 (0x10), the same bit that greys the journal's Share button out. Value unused.
		MainQuestCanNotShare = 2,

		// "ShareFeedbackLegitTargetsOne" / "...Many" - the share went out to Value team-mates. The client picks the
		// singular key when Value == 1 and the plural (with the number substituted) otherwise, so Value is a COUNT
		// and 1 is not a flag.
		LegitTargets = 5,

		// "ShareFeedbackOutOfRangeTargetsOne" / "...Many" - Value team-mates were too far away to be offered it.
		// Same singular/plural split as LegitTargets.
		OutOfRangeTargets = 6,

		// "ShareFeedbackNoLegitTargets" - nobody in the team could be offered the quest. Value unused.
		NoLegitTargets = 7,

		// ---- individual (0x2049): about ONE player; Value is that player's NAME ------------------------------

		// "ShareFeedbackLevelTooLow" - the named player is under the quest's level band.
		LevelTooLow = 3,

		// "ShareFeedbackLevelTooHigh" - the named player is over it.
		LevelTooHigh = 4,

		// "ShareFeedbackQuestDeclined" - the named player pressed No on the share window (their c2s ShareQuestDeclined).
		QuestDeclined = 8,

		// "ShareFeedbackPlayerBusy" - the named player's client refused to raise the window (their c2s ShareQuestBusy).
		PlayerBusy = 9,

		// "ShareFeedbackAlreadyFinished" - the named player has already completed the quest.
		AlreadyFinished = 10,

		// "ShareFeedbackDoesNotHaveExpansion" - the named player's account lacks the expansion the quest belongs to.
		DoesNotHaveExpansion = 11
	}
}
