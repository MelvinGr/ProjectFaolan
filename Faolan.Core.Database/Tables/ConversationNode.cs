using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One NPC-conversation SCREEN, identified by (NpcName, TypeId, AnswerPath) + the quest-state gate - the player's
	// path through the dialogue tree. Holds the response packets to send (ordered), fully structured since #188.
	// Imported from captures by Faolan.Importer; served by ConversationService (which routes a live KnubotAnswer
	// index along the AnswerPath). Identified by NPC NAME (stable), not the session-volatile handle. A screen can
	// have quest-state-gated variants under one (name, type, path), so the condition columns are part of its identity.
	//
	// The PK is DERIVED from that identity (ComputeId), never store-generated. The importer delete+reinserts this
	// whole table on every re-harvest, so an AUTOINCREMENT surrogate renumbered all 242 nodes - and, through the
	// packet/screen/parameter children, ~8.8k rows - on every run, churning faolan.sql (the reviewable data mirror,
	// #411) for zero semantic change. A derived id is byte-stable across re-imports instead. It also ENFORCES the
	// identity, which the old unique index could not: two of its columns are nullable and SQL treats NULLs as
	// distinct, so 232 of the 242 rows (the unconditional ones) were never constrained by it at all. Identical
	// identities now collide on the PK. The index that remains is the runtime lookup path (GetConversationScreens),
	// not a uniqueness gate.
	[Index(nameof(NpcName), nameof(TypeId), nameof(AnswerPath))]
	public class ConversationNode : IDerivedKey
	{
		[Key]
		[DatabaseGenerated(DatabaseGeneratedOption.None)]
		public uint Id { get; set; }

		// The QUEST-STATE gate for this screen variant - how the SAME (NpcName, TypeId, AnswerPath) serves different
		// dialogue by quest state (e.g. Casilda's pre- vs post-first-quest greeting). NULL = unconditional (the fallback).
		// When set, the variant applies only while the player holds quest ConditionQuestId in state ConditionQuestState
		// (Active = accepted-not-completed, Completed = turned in). The runtime picks the first variant whose gate passes,
		// else the unconditional one. The importer tags each captured variant with the state it was captured in
		// (ConversationConditions). Replaced the old free-form Lua condition source (issue #37); the two shapes retail's
		// Lua actually used - HasCompletedQuest(X) and HasQuest(X) and not HasCompletedQuest(X) - are exactly these two
		// columns, so no interpreter is needed.
		public uint? ConditionQuestId { get; set; }

		// The RDB quest id this screen GRANTS when reached (NULL = none). Harvested from the captures: a QuestFullUpdate /
		// QuestIIR-add for a quest arrived while this screen's responses were being collected (i.e. the player walked
		// the accept branch). When the runtime serves this screen it persists the quest (active) for the player and
		// sends its journal content - this is how "talk to NPC -> accept quest -> it appears in the J-panel" works,
		// generically for any NPC (not just Casilda). See QuestDefinition / WorldBroadcaster.SendQuestFullUpdate.
		public uint? GrantQuestId { get; set; }

		[ForeignKey(nameof(GrantQuestId))]
		public virtual QuestDefinition? GrantQuest { get; set; }

		// The NPC's TYPE id (NpcStats.template_id from the same spawn) - the STABLE per-type key the runtime gates the
		// conversation on (#244). Needed now that same-named conversational NPCs exist: "Halen van Dummy" is the internal
		// name of 25 templates (the quest-giver Atraxus 12236 + children/dummies), so a name match ALONE would open
		// Atraxus's dialogue on any of them. Null when the conversation NPC's spawn (its template) wasn't in the capture -
		// then the runtime falls back to name-only matching (create-flow scripted intros, etc.).
		// Nullable BY DESIGN: the world is pruned, so a conversation NPC's TYPE may not be imported (e.g. the Conarch
		// Village Guard's 42008 tree) - null there means "gate off, match by name only" rather than dropping the dialogue.
		// NOT an identity column: it is a gate the name already determines (35 names, 35 name/template pairs), and a
		// nullable column cannot key a row.
		public uint? NpcTemplateId { get; set; }

		[ForeignKey(nameof(NpcTemplateId))]
		public virtual NpcTemplate? NpcTemplate { get; set; }

		public uint TypeId { get; set; }

		// TRUE when retail opened this conversation ITSELF - a KnubotOpenChatWindow with no player TalkToNpc in front
		// of it - rather than the player clicking the NPC (#523). Casilda's city-door farewell is the worked example:
		// the player opens the door, the escort goal completes, and the server then opens her window unprompted.
		//
		// It is NOT reachable by talking, so the runtime's talk lookup must never serve it; it needs whatever EVENT
		// triggered it (TriggerWorldObjectId below, for the interaction kind). Nothing serves these yet - the point of
		// #523 was that they were being DROPPED, taking their {resolvegoal}/{gameactiononnpc} payload with them.
		//
		// It SELECTS which identity ComputeId uses rather than being hashed into it: a server-opened node is identified
		// by its trigger, a talk node is not. See ComputeId.
		public bool ServerInitiated { get; set; }

		// For a ServerInitiated node: the WORLD OBJECT whose use opened this conversation (#523). Null on a talk node,
		// and null on a server-opened one whose trigger is not an interaction - five of the seven in the corpus are
		// preceded by movement alone, so they are proximity-triggered and need a different target.
		//
		// Casilda's city-door farewell is the worked example: ActionType.UseObject (0x12) on WorldObjects.Id 1044692756
		// (map 4010, `object_closed`, DestinationMapId 4020 - the zone line into Tortage), reproduced in both
		// beach-to-city captures. SetNull rather than Cascade: losing the object should degrade the conversation to
		// "trigger unknown", not delete the dialogue.
		public uint? TriggerWorldObjectId { get; set; }

		[ForeignKey(nameof(TriggerWorldObjectId))]
		public virtual WorldObject? TriggerWorldObject { get; set; }

		public virtual ICollection<ConversationPacket> Packets { get; set; } = [];
		public QuestState? ConditionQuestState { get; set; }

		// The answer-index path to this screen within the NPC's dialogue: "" = the greeting, "0" = after picking
		// option 0, "0>1" = then option 1, ... The runtime tracks the live player's path and serves the matching
		// screen. This is the STABLE branch key (the old Mode/Field3 were session-volatile counters).
		[MaxLength(128)]
		public string AnswerPath { get; set; } = null!;

		// The NPC's name, resolved at import from the captured FA015807 spawn with this handle - the identity the
		// runtime routes on (GetConversationScreens), gated by NpcTemplateId. Never null: a node whose NPC could not
		// be named from its capture stores the "#<handle>" fallback the importer's merge already dedups on, so it
		// stays a distinct, addressable identity rather than a NULL no key can constrain.
		[MaxLength(128)]
		public string NpcName { get; set; } = null!;

		// The node's identity, hashed into its primary key (DerivedKey). Every component is part of what makes this
		// screen a DISTINCT screen, so two nodes agreeing on all of them are the same node and must collide. The
		// importer asserts the ids it computes are distinct, so a collision fails the import rather than silently
		// dropping a screen.
		// ⚠️ The trigger joins the identity ONLY for a SERVER-INITIATED node, and that asymmetry is deliberate. A
		// server-opened conversation is identified by WHAT FIRED IT - Casilda has two, and both sit at AnswerPath "",
		// so without the trigger one merges away (her `{hidegoodbye}Friend?` opener lost to the other). A talk node is
		// identified by what the player clicked and walked, and carries no trigger at all.
		//
		// Making it unconditional would have been simpler and much worse: DerivedKey.From NUL-JOINS its parts, so an
		// extra component - even a null one - shifts EVERY hash, re-keying all 467 talk nodes and cascading through
		// ~17k packet/screen/parameter children for no semantic change. Branching keeps this a pure function of the
		// row's own columns, which is all the derived-key contract asks.
		public static uint ComputeId(string npcName, uint typeId, string answerPath, uint? conditionQuestId, QuestState? conditionQuestState,
			bool serverInitiated = false, uint? triggerWorldObjectId = null)
		{
			return serverInitiated
				? DerivedKey.From(npcName, typeId, answerPath, conditionQuestId, conditionQuestState, triggerWorldObjectId)
				: DerivedKey.From(npcName, typeId, answerPath, conditionQuestId, conditionQuestState);
		}

		public uint ComputeId()
		{
			return ComputeId(NpcName, TypeId, AnswerPath, ConditionQuestId, ConditionQuestState, ServerInitiated, TriggerWorldObjectId);
		}

		public override string ToString()
		{
			return $"{Id} [{nameof(ConversationNode)}] {NpcName} '{AnswerPath}'";
		}
	}
}
