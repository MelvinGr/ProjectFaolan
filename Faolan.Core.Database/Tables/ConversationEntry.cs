using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Faolan.Core.Database.Attributes;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// A ROOT of an NPC's dialogue graph (#642): what OPENS the conversation, and on which screen it opens.
	//
	// It replaces the `AnswerPath == ""` idiom plus the `ServerInitiated` / `TypeId 0` pair that used to encode the
	// same thing across three columns of the node. The three ways retail opens a window are now one enum, and the four
	// runtime lookups that differed only in which of those columns they filtered on collapse into one.
	[Index(nameof(NpcTemplateId))]
	public class ConversationEntry : IDerivedKey, IQuestGated
	{
		[Key]
		[DatabaseGenerated(DatabaseGeneratedOption.None)]
		public uint Id { get; set; }

		// WHOSE root this is: the NPC's TEMPLATE id, and part of the entry's identity (#646). Same reasoning as
		// ConversationNode's - a display name is ambiguous in the world and cannot key a root, and this used to be a
		// nullable gate beside a name key, which is the wrong half to have kept.
		public uint NpcTemplateId { get; set; }

		[ForeignKey(nameof(NpcTemplateId))]
		public virtual NpcTemplate NpcTemplate { get; set; } = null!;

		// The NPC's display name - a LABEL for logs and reports, never routed on (#646).
		[MaxLength(128)]
		public string NpcName { get; set; } = null!;

		[ConstrainToEnumValues]
		public ConversationTrigger Trigger { get; set; }

		// For a UseWorldObject trigger: the WORLD OBJECT whose use opened this conversation (#523). Null otherwise.
		//
		// Casilda's city-door farewell is the worked example: BehaviorId.UseDynel (0x12) on WorldObjects.Id 1044692756
		// (map 4010, `object_closed`, DestinationMapId 4020 - the zone line into Tortage), reproduced in both
		// beach-to-city captures. SetNull rather than Cascade: losing the object should degrade the conversation to
		// "trigger unknown", not delete the dialogue.
		public uint? TriggerWorldObjectId { get; set; }

		[ForeignKey(nameof(TriggerWorldObjectId))]
		public virtual WorldObject? TriggerWorldObject { get; set; }

		// The quest-state gate on this ROOT - which of an NPC's greetings a player gets. Same rule as
		// ConversationEdge's: NULL is the unconditional fallback, gates can overlap, and the runtime takes the one
		// whose quest was accepted most recently.
		public uint? ConditionQuestId { get; set; }

		[ConstrainToEnumValues]
		public QuestState? ConditionQuestState { get; set; }

		// The GREETING-COUNT gate (#645): retail's NPCs open with a full welcome the first time a player opens their
		// window and a terse line every time after - Elethia's "Hello.{social greet}" becomes "What now?", Sancha's
		// "Anything for a paying customer." becomes "Hmm? Yes?". NULL is the unconditional root (nearly all of them:
		// the split is conflict-driven, so only an NPC the corpus caught saying two different things gets one), false
		// serves only on a first meeting, true only on a repeat.
		//
		// ⚠️ NOT SETTLED, and the competing reading is that retail simply picks a greeting at RANDOM. What the corpus
		// says, over all 21 sequences where one capture opens an NPC's window more than once:
		//   - a line NEVER recurs once a different one has appeared (0 of ~12 opportunities). Random predicts A>B>A.
		//   - 8 NPCs greeted 2-4 times give the SAME line every time (Turach four times), so re-greeting does not by
		//     itself reroll anything.
		//   - of the four NPCs this gate covers, only two are informative: Sancha and Elethia, both A>B>B. Dathos and
		//     Ulfheoinn were opened twice, and with two openings and two lines the first ALWAYS differs, so they say
		//     nothing. Two informative cases is 9:1 for a counter over a uniform draw - suggestive, not decisive.
		// The text is what tips it: `{social greet}` is on the first line and absent from the second, and "What now?" /
		// "Hmm? Yes?" are not first-contact lines. This model also reproduces every captured sequence exactly, which a
		// random pick does not. Nothing in a capture can separate the two outright, so treat it as the best-supported
		// reading rather than a decoded fact.
		//
		// Whether retail's own flag is per SESSION or per character for life is likewise undecidable here - no capture
		// has one character meet one of these four NPCs across two dumps - so the runtime holds it per session, which
		// is the reading a single capture supports.
		public bool? RepeatGreeting { get; set; }

		public uint NodeId { get; set; }

		[ForeignKey(nameof(NodeId))]
		public virtual ConversationNode Node { get; set; } = null!;

		public static uint ComputeId(uint npcTemplateId, ConversationTrigger trigger, uint? triggerWorldObjectId, uint? conditionQuestId, QuestState? conditionQuestState,
			bool? repeatGreeting)
		{
			return DerivedKey.From(npcTemplateId, (uint)trigger, triggerWorldObjectId, conditionQuestId, conditionQuestState, repeatGreeting switch
			{
				null => null,
				false => (uint?)0,
				true => 1
			});
		}

		public uint ComputeId()
		{
			return ComputeId(NpcTemplateId, Trigger, TriggerWorldObjectId, ConditionQuestId, ConditionQuestState, RepeatGreeting);
		}

		public override string ToString()
		{
			return $"{Id} [{nameof(ConversationEntry)}] {NpcName} ({NpcTemplateId}) {Trigger} -> {NodeId}";
		}
	}

	// How retail opens the conversation. Two of the three are SERVER-initiated (#523) - there is no player TalkToNpc in
	// front of them, which is also why they carry no BCC action id and used to be marked by a TypeId of 0.
	public enum ConversationTrigger
	{
		// The player clicked the NPC (a BehaviorId.UseCharacter TalkToNpc). Every captured talk root carries BCC action
		// 42008, so the action id is the trigger and needs no column of its own.
		Talk = 0,

		// The player USED a world object and the server then opened the window - Casilda's city-door farewell.
		UseWorldObject = 1,

		// The player walked up to the NPC. Saddur's beach confrontation ("{hidegoodbye}Who stands before Saddur?") is
		// the worked example. ⚠️ This is proximity to the NPC, NOT entry into a bounded area; only volume-entry lives in
		// RDB 1000053 and the two are different mechanisms.
		Approach = 2
	}
}
