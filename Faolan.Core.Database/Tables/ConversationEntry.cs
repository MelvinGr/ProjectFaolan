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

		public uint NodeId { get; set; }

		[ForeignKey(nameof(NodeId))]
		public virtual ConversationNode Node { get; set; } = null!;

		public static uint ComputeId(uint npcTemplateId, ConversationTrigger trigger, uint? triggerWorldObjectId, uint? conditionQuestId, QuestState? conditionQuestState)
		{
			return DerivedKey.From(npcTemplateId, (uint)trigger, triggerWorldObjectId, conditionQuestId, conditionQuestState);
		}

		public uint ComputeId()
		{
			return ComputeId(NpcTemplateId, Trigger, TriggerWorldObjectId, ConditionQuestId, ConditionQuestState);
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
