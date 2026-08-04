using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Faolan.Core.Database.Attributes;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One BRANCH of an NPC's dialogue graph (#642): clicking option `OptionId` on screen `From` serves screen `To`.
	//
	// The option is named by its own LDB string id - the same id the option button's remoteformat carries, `0` being
	// the universal "Goodbye" - not by the index it happened to occupy. An index is only meaningful relative to the
	// screen that was just sent, and the SAME screen offers different option lists in different quest states (Casilda's
	// greeting has six), so index `0` means six different things. The string id means one thing everywhere.
	//
	// Because the branch is stated rather than implied by an unrolled path, a menu that LOOPS BACK is just an edge to a
	// screen already in the graph. Three genuine cycles exist in the shipped data - Bhaltair of Lacheish (7 screens),
	// Guard Captain Marcus (4) and Hadrathus (2) - and under the old answer-path key each lap had to be re-captured;
	// going round twice closed the window on the player because the deeper path had never been walked.
	[Index(nameof(FromNodeId))]
	public class ConversationEdge : IDerivedKey, IQuestGated
	{
		[Key]
		[DatabaseGenerated(DatabaseGeneratedOption.None)]
		public uint Id { get; set; }

		public uint FromNodeId { get; set; }

		[ForeignKey(nameof(FromNodeId))]
		public virtual ConversationNode From { get; set; } = null!;

		// The clicked option's LDB string id (category 50001; 0 = "Goodbye", which 417 screens share).
		public uint OptionId { get; set; }

		public uint ToNodeId { get; set; }

		[ForeignKey(nameof(ToNodeId))]
		public virtual ConversationNode To { get; set; } = null!;

		// The QUEST-STATE gate on this BRANCH - which is where it belongs, rather than on the screen. Two harvested
		// walks reached Turach's `2832` by the same option with identical screen content, one granting quest 12371 and
		// one granting nothing; the difference is HOW you got there, not what the screen says.
		//
		// NULL = unconditional (the fallback). When set, the branch applies only while the player holds
		// ConditionQuestId in state ConditionQuestState. Several gates can pass at once - a player deep in a chain
		// still holds every earlier quest of it - so the runtime takes the branch gated on the quest accepted MOST
		// RECENTLY, and the unconditional one only when none passes (ConversationService.PickByLatestQuest, #639).
		public uint? ConditionQuestId { get; set; }

		[ConstrainToEnumValues]
		public QuestState? ConditionQuestState { get; set; }

		// The RDB quest id this branch GRANTS when taken (NULL = none). Harvested from the captures: a QuestFullUpdate
		// for a quest arrived while the destination screen's responses were being collected (i.e. the player walked the
		// accept branch). When the runtime takes the branch it persists the quest (active) for the player and sends its
		// journal content - this is how "talk to NPC -> accept quest -> it appears in the J-panel" works, generically.
		public uint? GrantQuestId { get; set; }

		[ForeignKey(nameof(GrantQuestId))]
		public virtual QuestDefinition? GrantQuest { get; set; }

		// The branch's identity: which screen it leaves, which option takes it, and under which gate. The DESTINATION is
		// deliberately not part of it - two edges agreeing on all three and disagreeing on the target are a contradiction
		// the import must fail on, not two rows to keep.
		public static uint ComputeId(uint fromNodeId, uint optionId, uint? conditionQuestId, QuestState? conditionQuestState)
		{
			return DerivedKey.From(fromNodeId, optionId, conditionQuestId, conditionQuestState);
		}

		public uint ComputeId()
		{
			return ComputeId(FromNodeId, OptionId, ConditionQuestId, ConditionQuestState);
		}

		public override string ToString()
		{
			return $"{Id} [{nameof(ConversationEdge)}] {FromNodeId} -{OptionId}-> {ToNodeId}";
		}
	}
}
