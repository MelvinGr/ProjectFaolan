using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Web.Components.Pages
{
	// A purpose-built editor for the quest chain (QuestDefinition + its QuestGoalDefinition goals): the SERVER-trigger
	// fields (GoalType/TargetName/TrailingRefId/RequiredCount + NextQuestId) that make a quest playable, the same ones
	// ImportOps.SeedGoal/Chain set. Sits next to the generic DbEditor but shows a quest and ALL its goals on one screen
	// with GoalType as a dropdown, so wiring a stage doesn't need raw SQL. Admin-gated. Edits the live DB (restart to
	// apply). The wire/@609 display fields (markers/keys) are shown but editing them can break byte-exact quests, so
	// they carry a warning.
	public partial class QuestEditor
	{
		private static readonly QuestGoalType[] GoalTypes = Enum.GetValues<QuestGoalType>();
		private List<QuestDefinition> _quests = [];
		private QuestDefinition? _quest;

		protected override async Task LoadAsync(DatabaseContext ctx)
		{
			_quests = await ctx.QuestDefinitions.Include(q => q.Goals).OrderBy(q => q.QuestId).ToListAsync();
			if (_quests.Count > 0)
				Select(_quests[0]);
		}

		private void AddGoal()
		{
			if (_quest == null)
				return;

			var nextSeq = _quest.Goals.Count == 0
				? 1u
				: _quest.Goals.Max(g => g.Sequence) + 1;
			var nextGoalId = _quest.Goals.Count == 0
				? 1u
				: _quest.Goals.Max(g => g.GoalId) + 1;
			_quest.Goals.Add(new QuestGoalDefinition
			{
				QuestDefinitionId = _quest.QuestId,
				GoalId = nextGoalId,
				Sequence = nextSeq,
				GoalType = QuestGoalType.Kill,
				RequiredCount = 1
			});
			Set("Added goal (unsaved — press Save)");
		}

		private IEnumerable<QuestDefinition> Filtered()
		{
			if (string.IsNullOrWhiteSpace(Filter))
				return _quests;

			var f = Filter.Trim();
			return _quests.Where(q =>
				q.QuestId.ToString().Contains(f, StringComparison.OrdinalIgnoreCase) || (q.NextQuestId?.ToString() ?? "").Contains(f, StringComparison.OrdinalIgnoreCase) ||
				q.Goals.Any(g => (g.TargetName ?? "").Contains(f, StringComparison.OrdinalIgnoreCase)));
		}

		private IEnumerable<QuestGoalDefinition> Goals()
		{
			return _quest?.Goals.OrderBy(g => g.Sequence).ThenBy(g => g.GoalId) ?? Enumerable.Empty<QuestGoalDefinition>();
		}

		// The successor a completed quest supersedes to (its NextQuestId), for the little chain hint under the header.
		private QuestDefinition? Next(QuestDefinition q)
		{
			return q.NextQuestId is { } n
				? _quests.FirstOrDefault(x => x.QuestId == n)
				: null;
		}

		private void RemoveGoal(QuestGoalDefinition g)
		{
			if (_quest == null)
				return;

			_quest.Goals.Remove(g);
			// A never-saved goal is only tracked as Added - dropping it from the nav is enough; a persisted goal must be
			// explicitly deleted so SaveChanges removes the row.
			if (Ctx != null && Ctx.Entry(g).State != EntityState.Added)
				Ctx.Remove(g);
			Set("Removed goal (unsaved — press Save)");
		}

		private void Select(QuestDefinition q)
		{
			_quest = q;
		}

	}
}
