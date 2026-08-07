using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Rdb;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Rdb
{
	// Fill a quest goal's `TargetName` from the TYPED reference the goal already carries (#521 work item 1).
	//
	// `TrailingCategory` 51000 is the client's own `npc_template.name` table (docs/rdb/ldb-string-index.md), and its id
	// space IS the npc template id - so a goal with that category names its target NPC twice over: as an id we can join,
	// and as a string the client ships in plaintext. Measured with `--report quest-goal-targets` over the whole catalog:
	// all 1,018 NpcTemplates have an LDB[51000] entry and 991 agree exactly with the name our captures recorded. The 27
	// that differ are ALL rows whose captured name is the placeholder `Halen van Dummy`, where the LDB carries the real
	// one - so the two id spaces line up and the string is not a separate "display name" that would break matching.
	//
	// ⚠️ Which source wins is nonetheless load-bearing, and it is the CATALOG, not the LDB. `TargetName` is a MATCH key:
	// it is compared against a live entity's name, which is `NpcTemplate.Name` (the name retail put on the wire).
	// Writing `Beggar` where the wire says `Halen van Dummy` would look resolved and silently never match - strictly
	// worse than the null it replaced. So:
	//   - template in our catalog -> its stored Name, i.e. exactly the string the runtime will compare against. The
	//     LDB is used only as the CHECK that the reference really is an npc_template ref.
	//   - template not in our catalog -> the LDB string, which is the only name that exists for it. It is inert until
	//     that map is imported, and harmless: the kill matcher resolves TrailingRefId == templateId first
	//     (QuestProgress.MatchingKillGoal), so the id branch takes over the moment the template lands.
	//
	// Categories 50200 (item), 51100 (categorytree) and 51200 (bounded_areas) are deliberately NOT written here. An item
	// name in a column the kill matcher reads as an NPC name would make it hunt a mob called `Stolen Goods`; 51100
	// resolves to editor GROUP names (`Watch`, `Undeads`, `Posha's men`) that match no NPC at all; and 51200 is a region,
	// whose geometry already lives in `QuestGoalDefinition.RegionId`.
	//
	// BOOTSTRAP-ONLY: a goal that already names a target is skipped, so a Quest-editor edit or a hand seed survives a
	// re-import.
	public static class RdbQuestGoalTargetImporter
	{
		private const uint NpcTemplateNameCategory = 51000;

		public static async Task<Result> ImportAsync(DatabaseContext db, string rdbDir, string? cdnBase)
		{
			Dictionary<(uint Category, uint Id), string> plaintext;
			using (var rdb = new RdbReader(rdbDir, cdnBase))
				plaintext = LdbStringIndex.EnglishPlaintext(rdb);

			var goals = await db
				.QuestGoalDefinitions.Where(g => g.TrailingCategory == NpcTemplateNameCategory && g.TrailingRefId > 0)
				.ToListAsync();

			var templates = await db.NpcTemplates.ToDictionaryAsync(t => t.TemplateId, t => t.Name);

			int fromCatalog = 0, fromLdb = 0, alreadyWired = 0, unresolved = 0, placeholderKept = 0;
			foreach (var goal in goals)
			{
				// The bootstrap guard, narrowed to what this rule actually writes - the goal's TargetName. A goal that
				// already names a target was configured by hand or by a prior run, and a goal typed something other
				// than a kill is not this rule's business.
				//
				// ⚠️ It cannot be the usual "is it still at the default" test any more (#522). A 51000 goal carrying
				// the client's kill verb is typed `Kill` before this runs (ImportOps.TypeKillGoalsFromKind), so a
				// not-yet-typed test would skip all 24 and the column would never be filled: `Kill` is a decision now
				// rather than the default. The TargetName test is what protects the two hand-seeded story kills, whose
				// GatesTargetVisibility this would otherwise clear.
				if (goal.TargetName != null || goal.QuestItemId != null || goal.GoalType is not (QuestGoalType.None or QuestGoalType.Kill))
				{
					alreadyWired++;
					continue;
				}

				if (!plaintext.TryGetValue((NpcTemplateNameCategory, goal.TrailingRefId), out var ldbName))
				{
					// The reference points at a string the shipped English pages do not carry. Counted rather than
					// skipped silently: this category resolved 24/24 when it was measured, so a miss is a regression in
					// the page walk, not an expected gap.
					unresolved++;
					continue;
				}

				if (templates.TryGetValue(goal.TrailingRefId, out var stored))
				{
					goal.TargetName = stored;
					fromCatalog++;
					if (!string.Equals(stored, ldbName, StringComparison.OrdinalIgnoreCase))
						placeholderKept++;
				}
				else
				{
					goal.TargetName = ldbName;
					fromLdb++;
				}

				// NOT set here, on purpose. Quest-gated visibility + NoRespawn is an editorial call about whether the
				// target is a one-time story NPC, and applying it to a mechanically-resolved population mob hides its
				// whole population and caps the kill count at however many spawns exist - see
				// QuestGoalDefinition.GatesTargetVisibility.
				goal.GatesTargetVisibility = false;
			}

			if (fromCatalog + fromLdb > 0)
				await db.SaveChangesAsync();

			return new Result(goals.Count, fromCatalog, fromLdb, alreadyWired, unresolved, placeholderKept);
		}

		// `PlaceholderKept` is the interesting one: the goal's template is one whose CAPTURED name is a shared
		// placeholder, so the catalog name we wrote deliberately disagrees with the LDB's real name. It matches, which
		// is what the column is for, but it reads wrong in the editor - so the run says how many.
		public readonly record struct Result(int Goals, int FromCatalog, int FromLdb, int AlreadyWired, int Unresolved, int PlaceholderKept);
	}
}
