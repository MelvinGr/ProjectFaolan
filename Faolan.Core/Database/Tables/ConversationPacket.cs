using System.ComponentModel.DataAnnotations.Schema;

namespace Faolan.Core.Database.Tables
{
	// One captured response packet of a ConversationNode, in send order (Sequence). Data is the raw on-wire Conan
	// packet; the runtime substitutes the live NPC handle + instance and recomputes the CRC before sending.
	public class ConversationPacket : BaseEntity
	{
		public uint ConversationNodeId { get; set; }

		[ForeignKey(nameof(ConversationNodeId))]
		public virtual ConversationNode Node { get; set; }

		public int Sequence { get; set; }

		public byte[] Data { get; set; }
	}
}
