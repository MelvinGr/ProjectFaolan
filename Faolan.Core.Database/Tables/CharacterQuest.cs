using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Faolan.Core.Database.Attributes;

namespace Faolan.Core.Database.Tables
{
	// A quest a character has in progress or completed (one row per character+quest). The quest DEFINITION (title /
	// description / journal text) lives in the client RDB and is rendered by id (the server only sends quest ids +
	// state via QuestIIR/QuestGoal/QuestFullUpdate); we just persist which quests a character has and their state +
	// objective progress. The full accept/branch logic is the pinned RDB Lua quest VM (docs/re/quests-and-combat.md).
	public class CharacterQuest : BaseEntity
	{
		public uint CharacterId { get; set; }

		[ForeignKey(nameof(CharacterId))]
		public virtual Character Character { get; set; } = null!;

		// The RDB quest id
		public uint QuestId { get; set; }

		[ConstrainToEnumValues]
		public QuestState State { get; set; } = QuestState.Active;

		// A kill objective: the NPC name whose death advances this quest's goal (null = no kill objective). The real
		// quest objective definitions live in the pinned RDB Lua quest VM; until then the target is set when the quest
		// is added, and the tracker advances GoalProgress on a matching kill.
		[MaxLength(128)]
		public string? GoalTargetName { get; set; }

		// Provisional single-objective progress (current/required). Multi-goal quests get richer modelling later.
		public uint GoalProgress { get; set; }
		public uint GoalRequired { get; set; }

		// Which pick-one-of-N reward this character took, as its 0-based position in the offer (#815). NULL means the
		// quest either offers no choice or the player has not answered yet.
		//
		// ⚠️ The guard is needed because the GRANT is driven by a c2s message, not by the completion. The quest does
		// complete exactly once - but `QuestRewardSelectionIIR` is an ordinary client packet naming its own quest id,
		// so a client can send it twice, or send one for a quest it finished last week, and each arrival would hand out
		// another item. Recording the answer is what makes the grant idempotent: the reward is spent once this is set,
		// whatever the client sends afterwards. Persisted rather than session-scoped for the same reason - the capture's
		// pick came ~7s after the offer, but nothing on the wire binds it to that window, so a relog must not reopen it.
		public uint? RewardChosenPos { get; set; }
	}

	public enum QuestState
	{
		Active = 0,
		Completed = 1
	}
}
