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
		public string NpcName { get; set; }
		public uint CapturedInstance { get; set; }

		public uint CapturedNpcHandle { get; set; }

		// The answer-index path to this screen within the NPC's dialogue: "" = the greeting, "0" = after picking
		// option 0, "0>1" = then option 1, ... The runtime tracks the live player's path and serves the matching
		// screen. This is the STABLE branch key (the old Mode/Field3 were session-volatile counters).
		public string AnswerPath { get; set; }
		public uint TypeId { get; set; }
	}
}
