namespace Faolan.Core.Database.Tables
{
	// A row that applies only while the player holds a quest in a given state - the two shapes retail's dialogue Lua
	// actually used, `HasCompletedQuest(X)` and `HasQuest(X) and not HasCompletedQuest(X)`, expressed as two columns
	// rather than an interpreter.
	//
	// Both ends of the dialogue graph carry one: a `ConversationEntry` decides which greeting an NPC gives, a
	// `ConversationEdge` decides where an option leads. They share the interface because they share the selection
	// rule - gates overlap, so the runtime takes the one whose quest was accepted most recently
	// (ConversationService.PickByLatestQuest) and the unconditional row only when none passes.
	public interface IQuestGated
	{
		uint? ConditionQuestId { get; }

		QuestState? ConditionQuestState { get; }
	}
}
