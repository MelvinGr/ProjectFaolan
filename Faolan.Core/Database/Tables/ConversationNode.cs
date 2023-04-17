using System.Collections.Generic;

namespace Faolan.Core.Database.Tables
{
	// One NPC-conversation node, keyed by the player's TalkToNpc args (TypeId, Mode, Field3). Holds the captured
	// response packets to replay (ordered), plus the captured NPC handle + player instance that the runtime
	// substitutes for the live ones. Imported from captures by Faolan.Importer; replayed by ConversationReplay.
	public class ConversationNode : BaseEntity
	{
		public uint TypeId { get; set; }
		public uint Mode { get; set; }
		public uint Field3 { get; set; }

		public uint CapturedNpcHandle { get; set; }
		public uint CapturedInstance { get; set; }

		public virtual ICollection<ConversationPacket> Packets { get; set; }
	}
}
