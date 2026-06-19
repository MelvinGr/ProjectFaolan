using System.Collections.Generic;

namespace Faolan.Core.Database.Tables
{
	// One NPC-conversation SCREEN, keyed by (NpcName, TypeId, AnswerPath) - the player's path through the dialogue
	// tree. Holds the captured response packets to replay (ordered), plus the captured NPC handle + player instance
	// that the runtime substitutes for the live ones. Imported from captures by Faolan.Importer; replayed by
	// ConversationReplay (which routes a live KnubotAnswer index along the AnswerPath).
	public class ConversationNode : BaseEntity
	{
		public virtual ICollection<ConversationPacket> Packets { get; set; }

		// The NPC's name, resolved at import from the captured FA015807 spawn with this handle. NPC dynel handles are
		// session-volatile (the world burst we replay and the conversation came from different captures), so routing
		// by handle fails; the name is the stable identity the runtime matches the clicked NPC against.
		// (#39 reviewed 2026-06-19: NpcStats.template_id from the spawn is the more *canonical* id, but it's redundant
		// here - name AND template_id are both derived from the same spawn parse on both the import and live sides, so
		// neither is more durable; every conversational NPC has a unique name; and the lookup is name-keyed end-to-end
		// with both sides matching the same ReadSpawnName output. Switch to template_id only if same-named conversational
		// NPCs ever appear - until then name is correct and the simpler key.)
		public string NpcName { get; set; }
		public uint CapturedInstance { get; set; }

		public uint CapturedNpcHandle { get; set; }

		// The answer-index path to this screen within the NPC's dialogue: "" = the greeting, "0" = after picking
		// option 0, "0>1" = then option 1, ... The runtime tracks the live player's path and serves the matching
		// screen. This is the STABLE branch key (the old Mode/Field3 were session-volatile counters).
		public string AnswerPath { get; set; }
		public uint TypeId { get; set; }

		// A Lua CONDITION source (run against the live player via LuaScriptVm.RunCondition) that gates this screen
		// variant - how the SAME (NpcName, TypeId, AnswerPath) serves different dialogue by quest/tag state (e.g.
		// Casilda's pre- vs post-first-quest greeting). "" = unconditional (the fallback / current behaviour). The
		// runtime picks the first variant whose condition passes for the player, else the "" one. Part of the
		// tag/quest dialogue VM (issue #37 / §6); the importer tags each captured variant with the state it came from.
		public string ConditionScript { get; set; } = "";

		// The RDB quest id this screen GRANTS when reached (0 = none). Harvested from the captures: a QuestFullUpdate /
		// QuestIIR-add for a quest arrived while this screen's responses were being collected (i.e. the player walked
		// the accept branch). When the runtime serves this screen it persists the quest (active) for the player and
		// sends its journal content - this is how "talk to NPC -> accept quest -> it appears in the J-panel" works,
		// generically for any NPC (not just Casilda). See QuestDefinition / WorldBroadcaster.SendQuestFullUpdate.
		public uint GrantQuestId { get; set; }
	}
}
