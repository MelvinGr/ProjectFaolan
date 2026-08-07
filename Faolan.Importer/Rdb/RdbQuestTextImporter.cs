using System;
using System.Collections.Generic;
using System.Linq;
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

		// Every pattern and the reading itself live on QuestObjectiveText (#704): the capture-driven wiring in
		// `ImportOps` has to ask the same questions of the PLAINTEXT catalog `Name` column, and one implementation is the
		// only way the two stay in step. The shapes it knows, and why each is there, are documented on that class -
		// including the "Ask X about Y" form, which is why quest 10287's "Ask Tina about Nadini" (goal 7363) sat `Partial`
		// and its quest presented in-client as a ONE-goal quest (#527).

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

				var read = QuestObjectiveText.Read(text);
				if (!isDeliver && !read.IsTalkShape)
				{
					notOurShape++;
					continue;
				}

				if (read.Recipient is not { } recipient)
				{
					noName++;
					continue;
				}

				// The precondition is RECORDED and the goal is still wired. Refusing to wire it would have thrown the
				// information away and needed a re-harvest the day the gate can be evaluated; carrying it means only
				// QuestProgress.PreconditionMet has to learn a new answer.
				if (read.RequiredLevel is { } lvl)
				{
					goal.RequiredLevel = lvl;
					gated++;
				}

				if (read.RequiredRankTitle is { } title)
				{
					goal.RequiredRankTitle = title;
					goal.RequiredRank = read.RequiredRank;
					gated++;
				}

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
