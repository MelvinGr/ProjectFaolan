using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One NPC-conversation SCREEN, keyed by (NpcName, TypeId, AnswerPath) - the player's path through the dialogue
	// tree. Holds the captured response packets to replay (ordered), plus the captured NPC handle + player instance
	// that the runtime substitutes for the live ones. Imported from captures by Faolan.Importer; replayed by
	// ConversationReplay (which routes a live KnubotAnswer index along the AnswerPath).
	// Keyed for dedup by NPC NAME (stable identity), not the session-volatile handle. A screen can have
	// quest-state-gated variants under one (name, type, path), so the condition columns are part of it.
	// NpcName is nullable and SQLite treats NULLs as distinct, so nodes whose NPC couldn't be named stay separate.
	[Index(nameof(NpcName), nameof(TypeId), nameof(AnswerPath), nameof(ConditionQuestId), nameof(ConditionQuestState), IsUnique = true)]
	public class ConversationNode : BaseEntity
	{
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
		public uint? NpcTemplateId { get; set; }

		[ForeignKey(nameof(NpcTemplateId))]
		public virtual NpcTemplate? NpcTemplate { get; set; }

		public uint TypeId { get; set; }
		public virtual ICollection<ConversationPacket> Packets { get; set; } = [];
		public QuestState? ConditionQuestState { get; set; }

		// The answer-index path to this screen within the NPC's dialogue: "" = the greeting, "0" = after picking
		// option 0, "0>1" = then option 1, ... The runtime tracks the live player's path and serves the matching
		// screen. This is the STABLE branch key (the old Mode/Field3 were session-volatile counters).
		[MaxLength(128)]
		public string AnswerPath { get; set; } = null!;

		// The NPC's name, resolved at import from the captured FA015807 spawn with this handle. Display/routing hint;
		// the runtime now gates on NpcTemplateId (below), not the name alone.
		[MaxLength(128)]
		public string? NpcName { get; set; }

		public uint CapturedInstance { get; set; }
		public uint CapturedNpcHandle { get; set; }
	}
}
