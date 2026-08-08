using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using Faolan.Core.Protobuf;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One `remoteformat` dialogue screen of a ConversationPacket (#188 columnization): the STRUCTURED form of the
	// captured <remoteformat id="I" category="C" key="K" knubot="N" {antitag="A"} > ... </remoteformat> XML, so the
	// runtime builds the wire XML from typed fields (RemoteFormatXml.Build / ConversationScreenColumns.ToScreen)
	// instead of storing it as text. A Knubot packet carries an ordered list of these (0..N; OpenChatWindow has
	// none). Byte-exact vs the captured screen (ConversationConstructTests). The live player name/guild are
	// regenerated into the "name"/"guildname" parameters at send time (RemoteFormatXml.WithLivePlayer).
	//
	// Keyed by its position in the tree - (node, packet, Sequence) - like its ConversationPacket parent, so a
	// re-import reproduces the same key (see ConversationNode). The leading two columns ARE the parent's key.
	[PrimaryKey(nameof(ConversationNodeId), nameof(PacketSequence), nameof(Sequence))]
	public class ConversationScreen
	{
		public uint ConversationNodeId { get; set; }

		public int PacketSequence { get; set; } // the owning ConversationPacket's Sequence

		[ForeignKey($"{nameof(ConversationNodeId)},{nameof(PacketSequence)}")]
		public virtual ConversationPacket Packet { get; set; } = null!;

		public uint ScreenId { get; set; } // remoteformat @id (0 for the close screen)

		public virtual ICollection<ConversationScreenParameter> Parameters { get; set; } = [];

		public int Sequence { get; set; } // order within the packet's screen list

		// The per-string LDB DECRYPTION KEY (LdbKey), stored as the 128-bit TEA key - the same ProtoDecryptionKey an
		// item template carries - rather than as the 20-character wire string, which LdbKey.ToWire regenerates from it
		// exactly, retail's attribute escaping included.
		//
		// ⚠️ Long described here as a "client-validated content hash" carried as raw attribute text. It is neither: the
		// client is not validating anything, it is DECRYPTING - eleven LDB categories are encrypted and this is the key
		// (#520). Text that came out blank was the client failing to decrypt, not failing a checksum.
		public virtual ProtoDecryptionKey Key { get; set; } = new ProtoDecryptionKey(); // @key

		public uint Category { get; set; } // @category

		public uint Knubot { get; set; } // @knubot
		public uint? Antitag { get; set; } // @antitag (null = attribute absent -> the "knubot=\"0\"  >" double-space slot)
	}
}
