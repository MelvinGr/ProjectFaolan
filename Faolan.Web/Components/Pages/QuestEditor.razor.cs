using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Web.Components.Pages
{
	// A purpose-built editor for the quest chain (QuestDefinition plus its QuestGoalDefinition goals): the
	// server-trigger fields (GoalType, TargetName, TrailingRefId, RequiredCount, NextQuestId) that make a quest
	// playable, the same ones QuestGoalWiring.SeedGoal and Chain set. It sits next to the generic DbEditor but shows a
	// quest and all its goals on one screen with GoalType as a dropdown, so wiring a stage needs no raw SQL.
	// Admin-gated, and it edits the live database, which takes a restart to apply. The wire display fields (markers,
	// keys) are shown but editing them can break byte-exact quests, so they carry a warning.
	//
	// Three columns are shown but not editable, because they are not the editor's to set: the goal's kind tag is the
	// client's own word for what the objective asks; the goal's region name is resolved from the typed reference the
	// row already carries; and the granting and gating conversations belong to the dialogue graph. Each answers a
	// question the editable fields raise - what the goal really is, where its trigger volume sits, and whether anything
	// grants the quest at all.
	public partial class QuestEditor
	{
		private static readonly QuestGoalType[] GoalTypes = Enum.GetValues<QuestGoalType>();
		private List<QuestDefinition> _quests = [];

		// The dialogue wiring that grants or gates a quest. Only the quest-linked rows are held, because that is the
		// point of showing them here: a quest nothing grants is unreachable in game, and that is invisible on the quest
		// row itself. Read-only; the graph is edited in the generic DbEditor.
		//
		// Two tables are involved - a quest is granted or gated by a branch (ConversationEdge) and an NPC's greeting is
		// gated by a root (ConversationEntry) - so they are projected into one row shape rather than shown twice.
		private List<QuestConversation> _questConversations = [];

		// Named trigger volumes, for resolving a goal's RegionId to something readable. 14 rows.
		private Dictionary<uint, MapRegion> _regions = [];
		private QuestDefinition? _quest;

		protected override async Task LoadAsync(DatabaseContext ctx)
		{
			_quests = await ctx.QuestDefinitions.Include(q => q.Goals).OrderBy(q => q.QuestId).ToListAsync();
			_regions = await ctx.MapRegions.ToDictionaryAsync(r => r.Id);
			var branches = await ctx.ConversationEdges
				.Include(e => e.From)
				.Where(e => e.GrantQuestId != null || e.ConditionQuestId != null)
				.Select(e => new QuestConversation(e.From.NpcName, e.From.NpcTemplateId, $"option {e.OptionId} out of 0x{e.FromNodeId:X8}", e.GrantQuestId, e.ConditionQuestId,
					e.ConditionQuestState, "branch", null))
				.ToListAsync();
			var roots = await ctx.ConversationEntries
				.Where(e => e.ConditionQuestId != null)
				.Select(e => new QuestConversation(e.NpcName, e.NpcTemplateId, $"{e.Trigger} root", null, e.ConditionQuestId, e.ConditionQuestState, e.Trigger.ToString(),
					e.TriggerWorldObjectId))
				.ToListAsync();

			_questConversations = [.. branches.Concat(roots).OrderBy(c => c.NpcName, StringComparer.Ordinal).ThenBy(c => c.Where, StringComparer.Ordinal)];
			// Open on something worth looking at rather than the lowest quest id, which has no goals at all: the quests
			// that have goals are the reason this page exists (SpellEditor opens the same way, for the same reason).
			var first = _quests.FirstOrDefault(q => q.Goals.Count > 0) ?? _quests.FirstOrDefault();
			if (first != null)
				Select(first);
		}

		// The conversations that reach this quest at all: the ones that grant it, and the ones whose screen is gated on
		// holding it. Both directions matter when wiring a chain - the grant is how the quest starts, the gate is what
		// the NPC says while it runs.
		private IEnumerable<QuestConversation> Conversations(QuestDefinition q)
		{
			return _questConversations.Where(c => c.GrantQuestId == q.QuestId || c.ConditionQuestId == q.QuestId);
		}

		// One row of the read-only wiring table: a branch that grants or gates the quest, or a root whose greeting is
		// gated on it. `Where` is the human-readable position in the graph - a branch names its option and the screen it
		// leaves, a root names what opens it.
		private sealed record QuestConversation(string NpcName, uint? NpcTemplateId, string Where, uint? GrantQuestId, uint? ConditionQuestId, QuestState? ConditionQuestState,
			string Trigger, uint? TriggerWorldObjectId);

		// The goal's own kind tag as the client states it - the wire's `Separator` word, decoded in QuestGoalKind.
		// Shown read-only beside the editable GoalType because it is the evidence the wiring should follow rather than a
		// second place to set it: it is captured data, and the corpus does not cover the client's full enum, so a value
		// outside the decoded set is rendered raw rather than guessed at.
		private static string KindLabel(uint separator)
		{
			return Enum.IsDefined((QuestGoalKind)separator)
				? $"{(QuestGoalKind)separator}"
				: $"({separator})";
		}

		// The name of a goal's trigger volume, or null when it carries none (only 3 goals in the catalog do; the rest
		// fall back to the marker-radius test).
		private string? RegionName(uint? regionId)
		{
			return regionId is { } id && _regions.TryGetValue(id, out var r)
				? r.Name
				: null;
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
				// `None` - no trigger - rather than a guess: the editor's job is to say what advances this goal, and
				// defaulting to a real trigger is how a goal ends up wired to something nobody chose.
				GoalType = QuestGoalType.None,
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
