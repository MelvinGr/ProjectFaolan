using Faolan.Core.Database.Tables;
using Faolan.GameServer.Dialogue;
using Xunit;

namespace Faolan.Tests
{
	// The quest-state-gated dialogue building block (issue #37): a ConversationNode carries a (ConditionQuestId,
	// ConditionQuestState) gate, and the runtime serves the variant whose gate matches the player's quest state
	// (unconditional = fallback). This locks the quest-gate evaluation that picks Casilda's pre- vs post-first-quest
	// dialogue. Replaced the old Lua-interpreter conditions - the two shapes retail's dialogue Lua used map exactly
	// onto (X, Completed) and (X, Active), so the gate is a plain state match, no interpreter.
	public class ConversationConditionTests
	{
		private static ConversationNode Gate(uint questId, QuestState state)
		{
			return new ConversationNode
			{
				ConditionQuestId = questId,
				ConditionQuestState = state
			};
		}

		private static CharacterQuest Held(uint questId, QuestState state)
		{
			return new CharacterQuest
			{
				QuestId = questId,
				State = state
			};
		}

		[Fact]
		public void CompletedGate_MatchesOnlyWhenCompleted()
		{
			var gate = Gate(13480, QuestState.Completed);

			Assert.True(ConversationService.QuestConditionMet(gate, [Held(13480, QuestState.Completed)]));
			Assert.False(ConversationService.QuestConditionMet(gate, [Held(13480, QuestState.Active)])); // has it, not done
			Assert.False(ConversationService.QuestConditionMet(gate, [])); // doesn't have it
		}

		[Fact]
		public void ActiveGate_MatchesOnlyWhileInProgress()
		{
			var gate = Gate(13479, QuestState.Active);

			Assert.True(ConversationService.QuestConditionMet(gate, [Held(13479, QuestState.Active)]));
			Assert.False(ConversationService.QuestConditionMet(gate, [Held(13479, QuestState.Completed)])); // already turned in
			Assert.False(ConversationService.QuestConditionMet(gate, [])); // not accepted
		}

		[Fact]
		public void Gate_IsQuestSpecific()
		{
			var gate = Gate(13480, QuestState.Completed);

			// Completing a DIFFERENT quest must not satisfy this gate.
			Assert.False(ConversationService.QuestConditionMet(gate, [Held(13479, QuestState.Completed)]));
		}
	}
}
