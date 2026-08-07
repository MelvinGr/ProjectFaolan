using System.Text.RegularExpressions;

namespace Faolan.Importer.Rdb
{
	// The SHAPES a quest goal's objective sentence takes, as the client writes them.
	//
	// The sentence reaches us from two sources that carry the same words: the ENCRYPTED LDB category 50304, readable
	// only for the 170 goals whose per-string key a capture recorded (RdbQuestTextImporter), and the PLAINTEXT
	// `QuestGoal.name` of the client's quest catalog (RDB 1000104), which is already a column on every row
	// (`QuestGoalDefinition.Name`, 7,299 of 7,317 goals) and needs no key and no RdbDir. Same sentence, two prices.
	//
	// So the patterns live here rather than on either reader: a rule that has only the plaintext column - the
	// capture-driven wiring in `ImportOps`, which deliberately runs without an RdbDir - must be able to ask the same
	// question the decrypted-text rule asks.
	public static class QuestObjectiveText
	{
		// A goal that ASKS the player to go and speak to someone: "Speak with Kern Wolfeye", "Report to Mei-zhu",
		// "Talk to King Conan of Aquilonia". The trigger is a greeting (TalkToNpc), never proximity.
		public static readonly Regex TalkObjective = new(@"^\s*(?:Talk|Speak|Report)\s+(?:to|with)\s", RegexOptions.IgnoreCase | RegexOptions.Compiled);

		// The other shape of the same objective: "ASK <someone> ABOUT <something>", where the name follows the VERB
		// instead of a to/with. `for` is accepted beside `about` - the verb is what makes it a talk goal.
		public static readonly Regex AskObjective = new(@"^\s*Ask\s+(?:an?\s+)?([A-Z][\w'\-]*(?:\s+[A-Z][\w'\-]*)?)\s+(?:about|for)\s", RegexOptions.Compiled);

		// An ESCORT: the objective is to walk the NPC somewhere, so it is emphatically not solved by standing at the
		// start marker - which is exactly where the escortee is when the goal is granted.
		//
		// ⚠️ `Escort` ALONE, measured over the 7,299 named catalog goals. The near-synonyms do not survive their own
		// data: all 10 `Escort …` goals are real escorts, but of the 8 `Lead …` two are not ("Lead the Fight Against
		// Atzel", "Lead the Corrupted into the Field of Stones and Observe") and of the 14 `Follow …` several are
		// plain reaches ("Follow the Tracks Into the Forest", "Follow the Trail of Clues"). Widening the verb costs a
		// correctly-typed reach for every escort it might catch, and `Guide` matches nothing at all.
		public static readonly Regex EscortObjective = new(@"^\s*Escort\s", RegexOptions.IgnoreCase | RegexOptions.Compiled);

		// Whether the objective sentence names a verb that a MAP MARKER cannot stand in for (#522).
		//
		// ⚠️ This exists because the marker is the WEAKEST evidence a goal carries and it was being read first. A goal
		// with a location reference and a map pin looks like a reach - but "Speak to Chulainn" carries a pin at
		// Chulainn's feet and "Escort Casilda to the city" carries one where Casilda is STANDING when the escort
		// starts. Typing either from its marker completes the objective by walking up to the NPC: seven talk goals
		// would close without their conversation ever playing, and the escort would close the instant it began.
		public static bool NeedsMoreThanReachingItsMarker(string? objective)
		{
			return objective is { Length: > 0 }
			       && (TalkObjective.IsMatch(objective) || AskObjective.IsMatch(objective) || EscortObjective.IsMatch(objective));
		}
	}
}
