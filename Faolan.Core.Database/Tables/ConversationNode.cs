using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Faolan.Core.Database.Tables
{
	// One NPC-conversation SCREEN, keyed by (NpcName, TypeId, AnswerPath) - the player's path through the dialogue
	// tree. Holds the captured response packets to replay (ordered), plus the captured NPC handle + player instance
	// that the runtime substitutes for the live ones. Imported from captures by Faolan.Importer; replayed by
	// ConversationReplay (which routes a live KnubotAnswer index along the AnswerPath).
	public class ConversationNode : BaseEntity
	{
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

		// The answer-index path to this screen within the NPC's dialogue: "" = the greeting, "0" = after picking
		// option 0, "0>1" = then option 1, ... The runtime tracks the live player's path and serves the matching
		// screen. This is the STABLE branch key (the old Mode/Field3 were session-volatile counters).
		[MaxLength(128)]
		public string AnswerPath { get; set; } = null!;

		// A Lua CONDITION source (run against the live player via LuaScriptVm.RunCondition) that gates this screen
		// variant - how the SAME (NpcName, TypeId, AnswerPath) serves different dialogue by quest/tag state (e.g.
		// Casilda's pre- vs post-first-quest greeting). "" = unconditional (the fallback / current behaviour). The
		// runtime picks the first variant whose condition passes for the player, else the "" one. Part of the
		// tag/quest dialogue VM (issue #37 / §6); the importer tags each captured variant with the state it came from.
		[MaxLength(256)]
		public string ConditionScript { get; set; } = "";

		// A Lua EFFECT source (run against the live player via LuaScriptVm.RunEffect) that fires when this screen is
		// served - the RDB quest engine's state mutation (player:SolveGoal/AddTag/SpawnItem/CastSpell). "" = no effect
		// (the current behaviour). The runtime persists the recorded effects: AddTag -> CharacterTag, SolveGoal ->
		// QuestProgressionService.SolveGoal (advances the owning quest). Closes the Lua effect->quest-state loop (#356)
		// so scripted accept/branch/turn-in advance without bespoke C#; the importer harvests it per reached screen.
		[MaxLength(256)]
		public string EffectScript { get; set; } = "";

		// The NPC's name, resolved at import from the captured FA015807 spawn with this handle. Display/routing hint;
		// the runtime now gates on NpcTemplateId (below), not the name alone.
		[MaxLength(128)]
		public string? NpcName { get; set; }

		public uint CapturedInstance { get; set; }
		public uint CapturedNpcHandle { get; set; }
	}
}
