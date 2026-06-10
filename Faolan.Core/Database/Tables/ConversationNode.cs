using System.Collections.Generic;

namespace Faolan.Core.Database.Tables
{
	// One NPC-conversation node, keyed by the player's TalkToNpc args (TypeId, Mode, Field3). Holds the captured
	// response packets to replay (ordered), plus the captured NPC handle + player instance that the runtime
	// substitutes for the live ones. Imported from captures by Faolan.Importer; replayed by ConversationReplay.
	public class ConversationNode : BaseEntity
	{
		public virtual ICollection<ConversationPacket> Packets { get; set; }

		// The NPC's name, resolved at import from the captured FA015807 spawn with this handle. NPC dynel handles are
		// session-volatile (the world burst we replay and the conversation came from different captures), so routing
		// by handle fails; the name is the stable identity the runtime matches the clicked NPC against.
		public string NpcName { get; set; }
		public uint CapturedInstance { get; set; }

		public uint CapturedNpcHandle { get; set; }
		public uint Field3 { get; set; }
		public uint Mode { get; set; }
		public uint TypeId { get; set; }
	}
}
