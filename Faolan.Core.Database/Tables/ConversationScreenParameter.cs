using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One <parameter value="7;{inner}" name="{Name}" /> of a ConversationScreen (#188). InnerRaw is the once-unescaped
	// inner element - a <localized .../> or <stringentry .../> - which RemoteFormatXml.Build re-escapes into the
	// parameter value. The "name" / "guildname" parameters are the live player substitution slots (regenerated at
	// send time); the rest (localized grammar tokens, npcname/npcguildname) are fixed dialogue content.
	//
	// Keyed by its position in the tree - (node, packet, screen, Sequence) - like the rest of the conversation tree,
	// so a re-import reproduces the same key (see ConversationNode). The leading three columns ARE the parent's key.
	[PrimaryKey(nameof(ConversationNodeId), nameof(PacketSequence), nameof(ScreenSequence), nameof(Sequence))]
	public class ConversationScreenParameter
	{
		public uint ConversationNodeId { get; set; }

		public int PacketSequence { get; set; } // the owning ConversationPacket's Sequence

		public int ScreenSequence { get; set; } // the owning ConversationScreen's Sequence

		[ForeignKey($"{nameof(ConversationNodeId)},{nameof(PacketSequence)},{nameof(ScreenSequence)}")]
		public virtual ConversationScreen Screen { get; set; } = null!;

		public int Sequence { get; set; } // order within the screen's parameter list

		[MaxLength(512)]
		public string InnerRaw { get; set; } = "";

		[MaxLength(64)]
		public string Name { get; set; } = "";
	}
}
