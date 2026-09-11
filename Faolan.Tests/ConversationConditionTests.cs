using Faolan.Core.Database.Tables;
using Faolan.GameServer.Dialogue;
using Xunit;

namespace Faolan.Tests
{
	// The quest-state-gated dialogue building block: both ends of the dialogue graph carry a
	// (ConditionQuestId, ConditionQuestState) gate - a ConversationEntry decides which greeting an NPC gives, a
	// ConversationEdge decides where an option leads - and the runtime takes the one whose gate matches the player's
	// quest state (unconditional = fallback). This locks the gate evaluation that picks Casilda's pre- vs
	// post-first-quest dialogue. The two shapes retail's dialogue Lua uses map exactly onto (X, Completed) and
	// (X, Active), so the gate is a plain state match rather than an interpreter.
	public class ConversationConditionTests
	{
		[Fact]
		public void ActiveGate_MatchesOnlyWhileInProgress()
		{
			var gate = Gate(13479, QuestState.Active);

			Assert.True(ConversationService.QuestConditionMet(gate, [Held(13479, QuestState.Active)]));
			Assert.False(ConversationService.QuestConditionMet(gate, [Held(13479, QuestState.Completed)])); // already turned in
			Assert.False(ConversationService.QuestConditionMet(gate, [])); // not accepted
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
		public void Gate_IsQuestSpecific()
		{
			var gate = Gate(13480, QuestState.Completed);

			// Completing a different quest must not satisfy this gate.
			Assert.False(ConversationService.QuestConditionMet(gate, [Held(13479, QuestState.Completed)]));
		}

		// Gates overlap by construction: the importer derives them from the quest state each variant was
		// captured in, and a player deep in a chain still holds every earlier quest of it. Casilda's escort greeting is
		// gated on 12407 Active and the two greetings after it sit on top of a 12407 that is still active, so all three
		// pass at once. The beat the player is on is the quest they accepted most recently.
		[Fact]
		public void OverlappingGates_ServeTheMostRecentlyAcceptedQuest()
		{
			var escort = Gate(12407, QuestState.Active);
			var later = Gate(12369, QuestState.Active);

			// CharacterQuest is insert-ordered, so the higher row id is the quest accepted later.
			var quests = new[] { Held(12407, QuestState.Active, id: 4), Held(12369, QuestState.Active, id: 9) };

			Assert.Same(later, ConversationService.PickByLatestQuest([escort, later], quests));
			Assert.Same(later, ConversationService.PickByLatestQuest([later, escort], quests)); // order-independent
		}

		// Once the later quest is turned in, its Active gate stops passing and the escort greeting is served again.
		[Fact]
		public void OverlappingGates_FallBackToTheEarlierBeatWhenTheLaterGateStopsPassing()
		{
			var escort = Gate(12407, QuestState.Active);
			var later = Gate(12369, QuestState.Active);
			var quests = new[] { Held(12407, QuestState.Active, id: 4), Held(12369, QuestState.Completed, id: 9) };

			Assert.Same(escort, ConversationService.PickByLatestQuest([escort, later], quests));
		}

		// No gate passes -> the caller serves the unconditional variant.
		[Fact]
		public void NoGatePasses_PicksNothing()
		{
			Assert.Null(ConversationService.PickByLatestQuest([Gate(12407, QuestState.Active)], [Held(13479, QuestState.Active)]));
		}

		private static ConversationEdge Gate(uint questId, QuestState state)
		{
			return new ConversationEdge
			{
				ConditionQuestId = questId,
				ConditionQuestState = state
			};
		}

		private static CharacterQuest Held(uint questId, QuestState state, uint id = 0)
		{
			return new CharacterQuest
			{
				Id = id,
				QuestId = questId,
				State = state
			};
		}
	}
}
