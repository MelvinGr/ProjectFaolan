using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Faolan.Importer.Rdb
{
	// What a quest goal's objective sentence says: which shape it is, who it names, and any precondition it states.
	//
	// The sentence reaches us from two sources carrying the same words, at very different prices:
	//
	//   LDB category 50304, encrypted   170 goals - needs a per-string TEA key only a capture yields, plus an RdbDir
	//   `QuestGoal.name`,   plaintext  7,299 goals - RDB 1000104, already a column on every row as `Name`
	//
	// So the patterns and the reading live here rather than on either reader. `RdbQuestTextImporter` runs them
	// over the decrypted text; `ImportOps` runs them over the plaintext column, in the capture-driven `quest-wiring`
	// module which deliberately has no RdbDir. One implementation, or the two drift and only one gets each fix.
	public static class QuestObjectiveText
	{
		// A goal that ASKS the player to go and speak to someone: "Speak with Kern Wolfeye", "Report to Mei-zhu",
		// "Talk to King Conan of Aquilonia". The trigger is a greeting (TalkToNpc), never proximity.
		public static readonly Regex TalkObjective = new Regex(@"^\s*(?:Talk|Speak|Report)\s+(?:to|with)\s", RegexOptions.IgnoreCase | RegexOptions.Compiled);

		// The other shape of the same objective: "ASK <someone> about <something>", where the name follows the verb
		// instead of a to/with. `for` is accepted beside `about` - the verb is what makes it a talk goal.
		public static readonly Regex AskObjective = new Regex(@"^\s*Ask\s+(?:an?\s+)?([A-Z][\w'\-]*(?:\s+[A-Z][\w'\-]*)?)\s+(?:about|for)\s", RegexOptions.Compiled);

		// An ESCORT: the objective is to walk the NPC somewhere, so it is emphatically not solved by standing at the
		// start marker - which is exactly where the escortee is when the goal is granted.
		//
		// `Escort` alone, measured over the 7,299 named catalog goals. The near-synonyms do not survive their own
		// data: all 10 `Escort …` goals are real escorts, but of the 8 `Lead …` two are not ("Lead the Fight Against
		// Atzel", "Lead the Corrupted into the Field of Stones and Observe") and of the 14 `Follow …` several are
		// plain reaches ("Follow the Tracks Into the Forest", "Follow the Trail of Clues"). Widening the verb costs a
		// correctly-typed reach for every escort it might catch, and `Guide` matches nothing at all.
		public static readonly Regex EscortObjective = new Regex(@"^\s*Escort\s", RegexOptions.IgnoreCase | RegexOptions.Compiled);

		// The NPC in an objective line: the last "to <Name>" / "with <Name>" in it. last, not first, because the item
		// can contain one ("Deliver the Head of Athyr-Bast to Hashima") and the person always closes the phrase. A name
		// is one or two Capitalised words, allowing the apostrophes and hyphens real names carry (`Jen-ta`,
		// `Akil-Atsu`), which also stops it at the trailing qualifier retail appends ("at the refugee camp", "on the
		// Lacheish Plains").
		//
		// It requires a Capitalised word, so a sentence naming a role yields nothing - "Talk to the city guard",
		// "Talk to the Red Hand Sergeant". That is deliberate: those need the goal's marker to say which NPC is meant,
		// and 868 of 7,317 goals carry no marker at all.
		public static readonly Regex Recipient = new Regex(@"\b(?:to|with)\s+([A-Z][\w'\-]*(?:\s+[A-Z][\w'\-]*)?)", RegexOptions.Compiled);

		// The two precondition shapes the client states in a goal's text. They are recorded, not used to refuse the
		// wiring: the goal is typed normally and QuestProgress.PreconditionMet enforces it at the trigger, where the
		// live player is known. See QuestGoalDefinition.RequiredLevel.
		//   "Talk to Sharak (level 30)"
		public static readonly Regex LevelPrecondition = new Regex(@"\(\s*level\s+(\d+)\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

		//   "…to Itsing When You Have Become an Acolyte (Rank 1)"  -> title "Acolyte", rank 1
		public static readonly Regex RankPrecondition = new Regex(@"When You Have Become (?:an?\s+)?([A-Za-z][\w \-']*?)\s*\(\s*Rank\s+(\d+)\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

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

			// Match the name on the sentence with the precondition clause removed. "…to Itsing When You Have Become
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

		// A HAND-in: the player carries something to someone. Distinct from the pure talk shapes above because an item
		// changes hands, which is the whole difficulty - the trigger is still the talk, but it must be gated on
		// actually holding the goods (QuestProgress.CanHandIn).
		public static readonly Regex HandInObjective = new Regex(@"^\s*(?:Give|Hand(?:\s+in)?|Deliver|Return|Bring)\s+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

		// What a hand-in sentence hands over: the noun phrase between the verb and the "to <recipient>" that closes it.
		//
		// It is returned as a whole PHRASE, and the caller must match it against the item catalog exactly. Matching
		// any catalog name that merely appears somewhere in the sentence is far more productive and mostly wrong: over
		// the un-typed hand-ins it resolves 15 goals and about 40% of them are junk - "Give the Veteran's Sword to
		// Daorgan", "…Bow to Adha" and "…Shield to Sian" all collapse onto one item named "Veteran", and "Give Ingots
		// of Blue Silver to Jocund" resolves to "Silver". The strict form yields 9 and every one is right, which is
		// the trade this codebase makes every time: a wrong target reads as wired and can never fire.
		//
		// Null when the sentence is not a hand-in or names no recipient.
		public static string? HandOverPhrase(string? objective)
		{
			if (objective is not { Length: > 0 } || HandInObjective.Match(objective) is not { Success: true } verb)
				return null;

			// The last "to <Name>", because the item itself can contain one ("Deliver the Letter to Cul Chieftain",
			// where "Letter to Cul Chieftain" is the catalog item and Cul Chieftain is the recipient).
			var closers = Recipient.Matches(objective);
			if (closers.Count == 0)
				return null;

			var phrase = objective[verb.Length..closers[^1].Index].Trim();
			return LeadingArticle.Replace(phrase, "") is { Length: > 0 } stripped ? stripped : null;
		}

		private static readonly Regex LeadingArticle = new Regex(@"^(?:the|a|an|some)\s+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

		// A kill: the thing to kill is the noun phrase after the verb. "Kill Vanir Lookouts", "Slay Dogs",
		// "Kill guards in Villa Paetus".
		public static readonly Regex KillObjective = new Regex(@"^\s*(?:Kill|Slay|Slaughter|Defeat|Vanquish|Destroy)\s+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

		// A trailing prepositional clause - "…in Villa Paetus", "…from the battlefield". Trimmed as a second attempt,
		// never the first, so a target whose own name contains one ("Lurker in the Dark") is tried whole first.
		private static readonly Regex TrailingClause = new Regex(@"\s+(?:from|in|at|on|near|inside|around|within|for)\s+.*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

		// What a kill objective names, most specific first: the phrase whole, then with a trailing clause trimmed, then
		// each of those de-pluralised. The caller matches them against the NPC catalog in this order and takes the first
		// that resolves to exactly one template - the client writes the group plural ("Kill Vanir Lookouts") where the
		// template is singular, and a live entity's name is the singular one.
		//
		// Empty when the sentence is not a kill objective.
		public static IEnumerable<string> KillTargetCandidates(string? objective)
		{
			if (objective is not { Length: > 0 } || KillObjective.Match(objective) is not { Success: true } verb)
				yield break;

			var phrase = LeadingArticle.Replace(LeadingCount.Replace(objective[verb.Length..].Trim(), ""), "").Trim();
			if (phrase.Length == 0)
				yield break;

			foreach (var form in TrailingClause.IsMatch(phrase) ? new[] { phrase, TrailingClause.Replace(phrase, "") } : [phrase])
			{
				yield return form;
				if (form.EndsWith('s'))
					yield return form[..^1];
			}
		}

		private static readonly Regex LeadingCount = new Regex(@"^(?:\d+|one|two|three|four|five|six|seven|eight|nine|ten)\s+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

		// The amount an objective asks for, stated right after its verb: "Hand in 20 Yew", "Collect ten Frost Serpent
		// Skins", "Deliver 3 stone bricks to Turach". Null when the sentence states none.
		//
		// This is the one thing the catalog says that nothing else records. `RequiredCount` otherwise comes only from
		// the wire goal record, so a goal no capture has shown sits at 0 - which the runtime reads as 1
		// (`Math.Max(RequiredCount, 1)`), and "collect twenty" completes on the first one. 58 shipped goals were in
		// exactly that state.
		//
		// Both spellings, because the client uses both: a numeral, or the words one..ten.
		private static readonly Regex CountedObjective = new Regex(
			@"^\s*(?:Give|Hand(?:\s+in)?|Deliver|Return|Bring|Get|Collect|Gather|Obtain|Recover|Kill|Slay|Destroy)\s+" + @"(\d+|one|two|three|four|five|six|seven|eight|nine|ten)\s",
			RegexOptions.IgnoreCase | RegexOptions.Compiled);

		private static readonly string[] NumberWords = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten"];

		public static uint? StatedCount(string? objective)
		{
			if (objective is not { Length: > 0 } || CountedObjective.Match(objective) is not { Success: true } m)
				return null;

			var word = m.Groups[1].Value;
			if (uint.TryParse(word, out var n))
				return n > 0 ? n : null;

			var index = Array.FindIndex(NumberWords, w => string.Equals(w, word, StringComparison.OrdinalIgnoreCase));
			return index > 0 ? (uint)index : null;
		}

		// Whether the objective sentence names a verb that a map marker cannot stand in for.
		//
		// This exists because the marker is the WEAKEST evidence a goal carries and it was being read first. A goal
		// with a location reference and a map pin looks like a reach - but "Speak to Chulainn" carries a pin at
		// Chulainn's feet and "Escort Casilda to the city" carries one where Casilda is standing when the escort
		// starts. Typing either from its marker completes the objective by walking up to the NPC: seven talk goals
		// would close without their conversation ever playing, and the escort would close the instant it began.
		public static bool NeedsMoreThanReachingItsMarker(string? objective)
		{
			return objective is { Length: > 0 }
			       && (TalkObjective.IsMatch(objective) || AskObjective.IsMatch(objective) || EscortObjective.IsMatch(objective));
		}
	}
}
