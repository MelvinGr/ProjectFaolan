using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// Per-character progress on a SECONDARY goal of a multi-goal quest (Gitea #357). A quest's PRIMARY (first,
	// lowest-Sequence) goal keeps living in the scalar CharacterQuest.GoalProgress/GoalRequired - unchanged, so
	// single-goal quests and the byte-exact snapshot/turn-in paths are untouched. Goals BEYOND the first (a quest that
	// asks for two concurrent objectives, e.g. the beach starter 10238's goals 1448 + 3490) get one row here each,
	// keyed by (CharacterId, QuestId, GoalId). The quest completes only when the primary AND every secondary goal has
	// reached its QuestGoalDefinition.RequiredCount (QuestProgressionService.AllGoalsComplete). Rows are upserted lazily
	// on the first advance of a secondary goal - a missing row means progress 0. QuestId FKs QuestDefinition (unlike
	// CharacterQuest, whose captured completed-quest history is 94% undefined-quest ids so it keeps NO FK) - only
	// DEFINED multi-goal quests get secondary-goal rows, so the FK holds.
	[PrimaryKey(nameof(CharacterId), nameof(QuestId), nameof(GoalId))]
	public class CharacterQuestGoal
	{
		public uint CharacterId { get; set; }

		[ForeignKey(nameof(CharacterId))]
		public virtual Character Character { get; set; } = null!;

		// The RDB quest id this goal belongs to (matches CharacterQuest.QuestId / QuestGoalDefinition.QuestDefinitionId).
		public uint QuestId { get; set; }

		[ForeignKey(nameof(QuestId))]
		public virtual QuestDefinition Quest { get; set; } = null!;

		// The goal's id (QuestGoalDefinition.GoalId, now that table's PK) - the wire-visible objective id echoed in QuestGoal.
		public uint GoalId { get; set; }

		[ForeignKey(nameof(GoalId))]
		public virtual QuestGoalDefinition QuestGoal { get; set; } = null!;

		// How many of this goal's required count the character has done (current/RequiredCount from the definition).
		public uint Progress { get; set; }
	}
}
