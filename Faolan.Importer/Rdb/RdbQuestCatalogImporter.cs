using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Protobuf;
using Faolan.Importer.Cli;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Rdb
{
	// The client's own quest catalog, RDB type 1000104 (#569) - see docs/rdb/quest-catalog-1000104.md.
	//
	// This is the DEFINITION side of the quest system. Everything the DB held before came from CAPTURES, so it covered
	// exactly the quests some captured character happened to hold or complete; the catalog covers the whole game and
	// carries the designers' names in PLAINTEXT, needing neither an LDB page nor a captured decryption key.
	//
	// ⚠️ It ADDS and NAMES; it never overwrites captured wire data. A quest that already has a row keeps every
	// harvested scalar - only Name, the line membership and the line's own catalog columns are written. That matters
	// because the two sources disagree in kind: the wire is what the server actually sent, the catalog is what the
	// client ships, and where we have the former it is the better evidence.
	public static class RdbQuestCatalogImporter
	{
		public sealed record Result(
			int LinesAdded,
			int PlaceholderLines,
			int LinesNamed,
			int QuestsAdded,
			int QuestsNamed,
			int GoalsAdded,
			int GoalsNamed,
			int Rewards,
			int GoalsOrphaned);

		public static async Task<Result> ImportAsync(DatabaseContext db, string rdbDir)
		{
			var catalog = ReportCli.ReadQuestCatalog(rdbDir)
			              ?? throw new InvalidOperationException(
				              "RDB type 1000104 (RDBQUESTGIVERS) not found - is RdbDir pointing at the client's RDB folder?");

			// Distinct by id, LAST wins. A handful of ids repeat and some of those rows differ (2 lines, 15 goals);
			// there is no key that separates them, so one has to be chosen. This is the one place the report's
			// "do not silently dedup" warning is unavoidable - it is recorded in the result and in the docs rather
			// than hidden, and it affects 17 of 14560 rows.
			var lines = catalog.MainQuests.GroupBy(m => m.TemplateId).ToDictionary(g => g.Key, g => g.Last());
			var quests = catalog.Quests.GroupBy(q => q.TemplateId).ToDictionary(g => g.Key, g => g.Last());
			var goals = catalog.QuestGoals.GroupBy(g => g.TemplateId).ToDictionary(g => g.Key, g => g.Last());

			// ---- quest lines ----
			var existingLines = await db.QuestLines.ToDictionaryAsync(l => l.QuestLineId);
			int linesAdded = 0, linesNamed = 0;
			foreach (var (id, m) in lines)
			{
				if (!existingLines.TryGetValue(id, out var row))
				{
					row = new QuestLine { QuestLineId = id };
					db.QuestLines.Add(row);
					existingLines[id] = row;
					linesAdded++;
				}

				if (row.Name != m.Name)
					linesNamed++;

				row.Name = m.Name;
				row.FirstQuestId = m.FirstQuestTemplateId;
				row.LastQuestId = m.LastQuestTemplateId;
				row.Tier = m.Tier;

				// 0 means "no prerequisite"; a dangling target (798, 806 are not lines in the table) is still recorded,
				// because dropping it would hide a gap in the client's own data behind a NULL that means something else.
				row.PrevQuestLineId = m.PrevTemplateId == 0
					? null
					: m.PrevTemplateId;
			}

			// 63 of the catalog's quests point at a line id that its OWN `main_quest` list does not contain, across 20
			// distinct ids - the same flavour of gap as the two dangling `prev` targets. A placeholder line carries the
			// id and nothing else, which keeps those 63 quests rather than dropping them to satisfy an FK. Same pattern
			// ImportOps uses for a quest whose Map is not in the catalog.
			var placeholderLines = 0;
			foreach (var lineId in quests.Values.Select(q => q.MainQuestTemplateId)
				         .Where(id => id != 0 && !existingLines.ContainsKey(id))
				         .Distinct())
			{
				var row = new QuestLine { QuestLineId = lineId };
				db.QuestLines.Add(row);
				existingLines[lineId] = row;
				placeholderLines++;
			}

			if (placeholderLines > 0)
				Console.WriteLine($"rdb-quest-catalog: added {placeholderLines} placeholder quest line(s) the catalog references but does not define.");

			await db.SaveChangesAsync();

			// ---- quests ----
			// The line must exist before a quest points at it (FK), which the SaveChanges above guarantees.
			var existingQuests = await db.QuestDefinitions.ToDictionaryAsync(q => q.QuestId);
			int questsAdded = 0, questsNamed = 0;
			foreach (var (id, q) in quests)
			{
				if (!existingQuests.TryGetValue(id, out var row))
				{
					row = new QuestDefinition { QuestId = id };
					db.QuestDefinitions.Add(row);
					existingQuests[id] = row;
					questsAdded++;
				}
				else if (row.Verified)
				{
					// Hand-reviewed rows are the editor's, not an import's - the same contract as ConversationNode.
					continue;
				}

				if (row.Name != q.Name)
					questsNamed++;

				row.Name = q.Name;

				// Only fill the line when the row has none. A captured quest already carries the line the WIRE put it
				// in, and that is the better evidence; the catalog fills the ones no capture ever placed.
				if (row.QuestLineId == 0 && q.MainQuestTemplateId != 0 && existingLines.ContainsKey(q.MainQuestTemplateId))
					row.QuestLineId = q.MainQuestTemplateId;
			}

			await db.SaveChangesAsync();

			// ---- goals ----
			var existingGoals = await db.QuestGoalDefinitions.ToDictionaryAsync(g => g.GoalId);
			int goalsAdded = 0, goalsNamed = 0, orphaned = 0;
			foreach (var (id, g) in goals)
			{
				// A goal whose quest is in neither the catalog nor the DB would violate the FK. Counted, not skipped
				// silently - a non-zero count here is a real gap in the client's data worth looking at.
				if (!existingQuests.ContainsKey(g.QuestTemplateId))
				{
					orphaned++;
					continue;
				}

				if (!existingGoals.TryGetValue(id, out var row))
				{
					// Separator stays NULL: the catalog has no kind tag, and this goal has never been on the wire.
					// 0 would be a lie - there is no kind 0 (see QuestGoalKind) - and it is the value #548 removed.
					row = new QuestGoalDefinition { GoalId = id, QuestDefinitionId = g.QuestTemplateId, Separator = null };
					db.QuestGoalDefinitions.Add(row);
					existingGoals[id] = row;
					goalsAdded++;
				}

				if (row.Name != g.Name)
					goalsNamed++;

				row.Name = g.Name;
				row.Phase = g.Phase;
			}

			await db.SaveChangesAsync();

			// ---- line item rewards: a clean rebuild, the set is fully derived from the fixed client RDB ----
			await db.QuestLineRewards.ExecuteDeleteAsync();
			var rewards = new List<QuestLineReward>();
			foreach (var (id, m) in lines)
			{
				uint pos = 0;
				foreach (var (r, optional) in m.Rewards.Select(r => (r, false)).Concat(m.OptionalRewards.Select(r => (r, true))))
					rewards.Add(new QuestLineReward
					{
						QuestLineId = id,
						Pos = pos++,
						ItemTemplateId = r.TemplateId,
						RewardType = r.Type,
						Optional = optional
					});
			}

			db.QuestLineRewards.AddRange(rewards);
			await db.SaveChangesAsync();

			return new Result(linesAdded, placeholderLines, linesNamed, questsAdded, questsNamed, goalsAdded, goalsNamed, rewards.Count, orphaned);
		}
	}
}
