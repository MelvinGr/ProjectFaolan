using System.Text.RegularExpressions;

namespace Faolan.Importer.Rdb
{
	// What a quest goal's objective SENTENCE says: which shape it is, who it names, and any precondition it states.
	//
	// The sentence reaches us from two sources carrying the same words, at very different prices:
	//
	//   LDB category 50304, ENCRYPTED   170 goals - needs a per-string TEA key only a CAPTURE yields, plus an RdbDir
	//   `QuestGoal.name`,   PLAINTEXT  7,299 goals - RDB 1000104, already a column on every row as `Name`
	//
	// So the patterns and the reading live HERE rather than on either reader (#704). `RdbQuestTextImporter` runs them
	// over the decrypted text; `ImportOps` runs them over the plaintext column, in the capture-driven `quest-wiring`
	// module which deliberately has no RdbDir. One implementation, or the two drift and only one gets each fix.
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

		// The NPC in an objective line: the LAST "to <Name>" / "with <Name>" in it. LAST, not first, because the item
		// can contain one ("Deliver the Head of Athyr-Bast to Hashima") and the person always closes the phrase. A name
		// is one or two Capitalised words, allowing the apostrophes and hyphens real names carry (`Jen-ta`,
		// `Akil-Atsu`), which also stops it at the trailing qualifier retail appends ("at the refugee camp", "on the
		// Lacheish Plains").
		//
		// ⚠️ It requires a Capitalised word, so a sentence naming a ROLE yields nothing - "Talk to the city guard",
		// "Talk to the Red Hand Sergeant". That is deliberate: those need the goal's MARKER to say which NPC is meant,
		// and 868 of 7,317 goals carry no marker at all.
		public static readonly Regex Recipient = new(@"\b(?:to|with)\s+([A-Z][\w'\-]*(?:\s+[A-Z][\w'\-]*)?)", RegexOptions.Compiled);

		// The two PRECONDITION shapes the client states in a goal's text. They are RECORDED, not used to refuse the
		// wiring: the goal is typed normally and QuestProgress.PreconditionMet enforces it at the trigger, where the
		// live player is known. See QuestGoalDefinition.RequiredLevel.
		//   "Talk to Sharak (level 30)"
		public static readonly Regex LevelPrecondition = new(@"\(\s*level\s+(\d+)\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

		//   "…to Itsing When You Have Become an Acolyte (Rank 1)"  -> title "Acolyte", rank 1
		public static readonly Regex RankPrecondition = new(@"When You Have Become (?:an?\s+)?([A-Za-z][\w \-']*?)\s*\(\s*Rank\s+(\d+)\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

		// Everything one objective sentence states. `Recipient` is null when the sentence names nobody the pattern can
		// take - a role rather than a name - which is a different outcome from "not a talk objective at all", so both
		// are reported and the caller counts them apart.
		public readonly record struct ObjectiveRead(bool IsTalkShape, string? Recipient, uint? RequiredLevel, string? RequiredRankTitle, uint? RequiredRank);

		public static ObjectiveRead Read(string? objective)
		{
			if (objective is not { Length: > 0 })
				return default;

			var ask = AskObjective.Match(objective);
			var isTalk = ask.Success || TalkObjective.IsMatch(objective);

			var rank = RankPrecondition.Match(objective);
			var level = LevelPrecondition.Match(objective) is { Success: true } lvl ? uint.Parse(lvl.Groups[1].Value) : (uint?)null;

			// ⚠️ Match the NAME on the sentence with the precondition clause REMOVED. "…to Itsing When You Have Become
			// an Acolyte" otherwise yields "Itsing When": the name pattern takes up to two Capitalised words and `When`
			// is one, so the clause silently becomes part of the recipient and no NPC ever matches it.
			var nameText = rank.Success
				? objective[..rank.Index]
				: objective;

			// "Ask X about Y" names X directly after the verb; every other shape names it after a to/with.
			var named = ask.Success
				? ask.Groups[1].Value
				: Recipient.Matches(nameText) is { Count: > 0 } m
					? m[^1].Groups[1].Value
					: null;

			return new ObjectiveRead(isTalk, named?.Trim() is { Length: > 0 } who ? who : null,
				level, rank.Success ? rank.Groups[1].Value.Trim() : null, rank.Success ? uint.Parse(rank.Groups[2].Value) : null);
		}

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
