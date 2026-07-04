using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Faolan.Core.Database.Tables
{
	// A quest a character has in progress or completed (one row per character+quest). The quest DEFINITION (title /
	// description / journal text) lives in the client RDB and is rendered by id (the server only sends quest ids +
	// state via QuestIIR/QuestGoal/QuestFullUpdate); we just persist which quests a character has and their state +
	// objective progress. The full accept/branch logic is the pinned RDB Lua quest VM (docs/archive/quests-and-combat.md).
	public class CharacterQuest : BaseEntity
	{
		[ForeignKey(nameof(CharacterId))]
		public virtual Character Character { get; set; }

		[ConstrainToEnumValues]
		public QuestState State { get; set; } = QuestState.Active;

		// A kill objective: the NPC name whose death advances this quest's goal (null = no kill objective). The real
		// quest objective definitions live in the pinned RDB Lua quest VM; until then the target is set when the quest
		// is added (e.g. via .quest), and the tracker advances GoalProgress on a matching kill.
		[MaxLength(128)]
		public string GoalTargetName { get; set; }

		public uint CharacterId { get; set; }

		// Provisional single-objective progress (current/required). Multi-goal quests get richer modelling later.
		public uint GoalProgress { get; set; }
		public uint GoalRequired { get; set; }

		// The RDB quest id (e.g. 13479); the client renders the quest's text from this.
		public uint QuestId { get; set; }
	}

	public enum QuestState
	{
		Active = 0,
		Completed = 1
	}
}
