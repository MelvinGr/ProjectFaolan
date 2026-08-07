using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Faolan.Core.Crypt;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Rdb;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Rdb
{
	// Wires quest goals from their own DECRYPTED objective text (#520/#521).
	//
	// The client states what every goal asks for, in encrypted LDB category 50304, and we hold the per-string key on
	// the goal itself (`ObjectiveKey`). So the wiring that used to need a hand-written table is a RULE over the
	// client's own words: `Deliver` goals name their recipient, and this reads it.
	//
	// ⚠️ RDB-BACKED, therefore its own opt-in module rather than part of the capture-driven `quest-wiring`: the
	// ciphertext lives in the client's install, and the capture modules deliberately run without an RdbDir. Nothing is
	// lost when it does not run - the wiring is bootstrap-only and already-wired goals are skipped, so the rows stay as
	// they are.
	public static class RdbQuestTextImporter
	{
		// `quest_goal_templates.goal_name` - the goal's own display text.
		private const uint GoalNameCategory = 50304;

		// The NPC in an objective line: the LAST "to <Name>" / "with <Name>" in it. LAST, not first, because the item
		// can contain one ("Deliver the Head of Athyr-Bast to Hashima") and the person always closes the phrase. A name
		// is one or two Capitalised words, allowing the apostrophes and hyphens real names carry (`Jen-ta`,
		// `Akil-Atsu`), which also stops it at the trailing qualifier retail appends ("at the refugee camp", "on the
		// Lacheish Plains").
		private static readonly Regex Recipient = new(@"\b(?:to|with)\s+([A-Z][\w'\-]*(?:\s+[A-Z][\w'\-]*)?)", RegexOptions.Compiled);

		// A goal whose text ASKS the player to go and speak to someone - the same TalkToNpc trigger a delivery uses,
		// just without an item changing hands. "Speak with Kern Wolfeye", "Report to Mei-zhu", "Talk to King Conan of
		// Aquilonia".
		//
		// The two objective SHAPES live on QuestObjectiveText, because the capture-driven wiring has to ask the same
		// question of the PLAINTEXT catalog name (see that class) and cannot reach in here for it.
		private static readonly Regex TalkObjective = QuestObjectiveText.TalkObjective;

		// The other shape of the same objective: "ASK <someone> ABOUT <something>". It is a talk goal - the player
		// opens a greeting and nothing changes hands - but the name follows the VERB rather than a to/with, so
		// `Recipient` cannot find it and `TalkObjective` does not recognise it. That is why quest 10287's "Ask Tina
		// about Nadini" (goal 7363) sat `Partial` and its quest presented in-client as a ONE-goal quest (#527).
		//
		// Measured before it was added, over all 7,317 catalog goals: exactly THREE objectives begin with "Ask", and
		// the shape is not ambiguous with anything else -
		//   7363  "Ask Tina about Nadini"                                    -> Tina (NpcTemplate 2805)
		//   6597  "Ask Cavan about the Gambler"                              -> Cavan (9141); already typed, so the
		//                                                                       bootstrap guard above skips it
		//   11739 "Ask a Faction Contact for Information on the Assassinations" -> "Faction Contact", which is not an
		//                                                                       NPC we hold, so it lands in the
		//                                                                       withoutNpc count rather than silently
		// `for` is accepted beside `about` for that third one: the verb is what makes it a talk goal, and refusing the
		// preposition would only hide the one case worth counting.
		private static readonly Regex AskObjective = QuestObjectiveText.AskObjective;

		// The two PRECONDITION shapes the client states in a goal's text. They are RECORDED, not used to refuse the
		// wiring: the goal is typed normally and QuestProgress.PreconditionMet enforces it at the trigger, where the
		// live player is known. See QuestGoalDefinition.RequiredLevel.
		//   "Talk to Sharak (level 30)"
		private static readonly Regex LevelPrecondition = new(@"\(\s*level\s+(\d+)\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

		//   "…to Itsing When You Have Become an Acolyte (Rank 1)"  -> title "Acolyte", rank 1
		private static readonly Regex RankPrecondition = new(@"When You Have Become (?:an?\s+)?([A-Za-z][\w \-']*?)\s*\(\s*Rank\s+(\d+)\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

		public static async Task<Result> ImportAsync(DatabaseContext db, string rdbDir, string? cdnBase)
		{
			var ciphertext = EnglishEncryptedStrings(rdbDir, cdnBase);
			// Every goal, not just the deliveries: the same "who does this name" rule types a talk objective too.
			var goals = await db.QuestGoalDefinitions.ToListAsync();
			var npcNames = (await db.NpcTemplates.Select(t => t.Name).ToListAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);
			// A TalkToNpc goal advances only once a GREETING is served, so an NPC with no ConversationNode rows can never
			// complete one however correctly it is wired. Counted separately from "NPC missing" because the fix differs:
			// one needs the map's spawns, the other needs a capture of the dialogue.
			var withDialogue = (await db.ConversationNodes.Select(n => n.NpcName).Distinct().ToListAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);

			int wired = 0, unreadable = 0, gated = 0, noName = 0, notOurShape = 0, alreadyWired = 0, withoutNpc = 0, withoutDialogue = 0, reconciled = 0;
			foreach (var goal in goals)
			{
				// Bootstrap-only, like every other wiring step: a Quest-editor edit or a hand seed survives a re-import.
				// `None` is the un-typed state (#522) - it used to be `Kill`, which is why this also had to test the
				// TargetName to tell a real kill goal from a row nobody had typed.
				if (goal.GoalType != QuestGoalType.None)
				{
					alreadyWired++;
					continue;
				}

				// Only the two shapes this rule can type: a DELIVERY (kind 20 - hand the item over) and an objective
				// that asks the player to go and talk to someone. Everything else is left to its own decode.
				var isDeliver = goal.Separator == (uint)QuestGoalKind.Deliver;

				var category = goal.ObjectiveCategory == 0
					? GoalNameCategory
					: goal.ObjectiveCategory;
				if (!ciphertext.TryGetValue((category, goal.ObjectiveRefId), out var blob) || LdbKey.Decrypt(blob, goal.ObjectiveKey) is not { } text)
				{
					unreadable++;
					continue;
				}

				var ask = AskObjective.Match(text);
				if (!isDeliver && !TalkObjective.IsMatch(text) && !ask.Success)
				{
					notOurShape++;
					continue;
				}

				// The precondition is RECORDED and the goal is still wired. Refusing to wire it would have thrown the
				// information away and needed a re-harvest the day the gate can be evaluated; carrying it means only
				// QuestProgress.PreconditionMet has to learn a new answer.
				var rank = RankPrecondition.Match(text);
				if (LevelPrecondition.Match(text) is { Success: true } lvl)
				{
					goal.RequiredLevel = uint.Parse(lvl.Groups[1].Value);
					gated++;
				}

				if (rank.Success)
				{
					goal.RequiredRankTitle = rank.Groups[1].Value.Trim();
					goal.RequiredRank = uint.Parse(rank.Groups[2].Value);
					gated++;
				}

				// ⚠️ Match the NAME on the text with the precondition clause REMOVED. "…to Itsing When You Have Become an
				// Acolyte" otherwise yields "Itsing When": the name pattern takes up to two Capitalised words and `When`
				// is one, so the clause silently becomes part of the recipient and no NPC ever matches it.
				var nameText = rank.Success
					? text[..rank.Index]
					: text;

				// "Ask X about Y" names X directly after the verb; every other shape names it after a to/with.
				var match = Recipient.Matches(nameText).LastOrDefault();
				if (match == null && !ask.Success)
				{
					noName++;
					continue;
				}

				var recipient = (ask.Success ? ask : match!).Groups[1].Value.Trim();

				// ⚠️ RECONCILE the sentence's name against the catalog before storing it. `TargetName` is a MATCH KEY,
				// compared for EQUALITY against the live entity's name - and prose uses the short form where the world
				// uses the full one. Two goals shipped dead on exactly that: *"Talk to Bronwith in Conarch Village"*
				// stored `Bronwith`, which can never equal the live **Battlemaster Bronwith**, and *"Speak to Chael"*
				// stored `Chael` against **Tanner Chael**. Both read as wired and neither could ever fire.
				//
				// It is the same rule `RdbQuestGoalTargetImporter` already applies to the 51000 half - the CATALOG wins
				// wherever it exists, because the catalog is what the runtime compares against. Only an UNAMBIGUOUS
				// single template may claim the name (measured: no goal in the shipped data has two), so a title that
				// several NPCs share leaves the short form alone rather than picking one.
				if (!npcNames.Contains(recipient))
				{
					var titled = npcNames.Where(n => n.EndsWith(' ' + recipient, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
					if (titled.Count == 1)
					{
						recipient = titled[0];
						reconciled++;
					}
				}

				// The name is wired whether or not we hold that NPC. It is the client's own statement of who the goal
				// is about, so recording it is right regardless of spawn coverage - and the goal then works the moment
				// its map is imported, with no second pass through here. It is inert either way until then.
				//
				// ⚠️ Counted, not silent: a wired goal whose NPC is missing LOOKS wired, so the run says how many.
				if (!npcNames.Contains(recipient))
					withoutNpc++;
				else if (!withDialogue.Contains(recipient))
					withoutDialogue++;

				goal.GoalType = QuestGoalType.TalkToNpc;
				goal.TargetName = recipient;

				// A goal this rule types is now COMPLETE - kind, target and count - so it stops being partial and
				// starts counting. `Partial` is what `AllGoalsComplete`, `MatchingKillGoal` and
				// `QuestObjectiveRegion.Build` skip on, so leaving it set would wire the goal and still render the
				// quest as if it had one fewer objective (#527). Only cleared here, where the row is actually filled.
				if (goal.Partial && goal.RequiredCount > 0)
					goal.Partial = false;

				wired++;
			}

			if (wired > 0)
				await db.SaveChangesAsync();

			return new Result(goals.Count, wired, unreadable, gated, noName, notOurShape, alreadyWired, withoutNpc, withoutDialogue, reconciled);
		}

		// (category, id) -> ciphertext over the ENGLISH pages of the ENCRYPTED categories. The language comes from the
		// client's own language records rather than from the text (whole pages ship untranslated, so a sniffer unions
		// languages and yields French), and the encrypted set from the page header's own flags word.
		private static Dictionary<(uint Category, uint Id), byte[]> EnglishEncryptedStrings(string rdbDir, string? cdnBase)
		{
			using var rdb = new RdbReader(rdbDir, cdnBase);
			var language = LdbLanguageIndex.LanguageByPage(rdb);
			var blobs = new Dictionary<(uint, uint), byte[]>();

			foreach (var e in rdb.EntriesOfType(LdbLanguageIndex.PageType).Where(rdb.CanRead))
			{
				if (!language.TryGetValue(e.Id, out var lang) || lang != LdbLanguageIndex.English)
					continue;

				byte[] body;
				try
				{
					body = rdb.ReadDecompressed(e);
				}
				catch
				{
					continue;
				}

				if (LdbStringIndex.CategoryOf(body) == 0 || LdbStringIndex.FlagsOf(body) != 1)
					continue;

				foreach (var entry in LdbStringIndex.ParseRaw(body))
					blobs[(entry.Category, entry.Id)] = entry.Bytes;
			}

			return blobs;
		}

		// Every goal is accounted for by REASON - wired, or one of the five ways it was not. A silent skip here would be
		// indistinguishable from a decode regression, so the counts are expected to sum to DeliverGoals.
		public readonly record struct Result(int Goals, int Wired, int Unreadable, int PreconditionGated, int NoNameInText, int NotOurShape, int AlreadyWired, int WiredWithoutNpc, int WiredWithoutDialogue,
			int ReconciledToTheCatalogName);
	}
}
