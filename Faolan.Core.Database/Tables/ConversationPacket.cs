using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;

namespace Faolan.Core.Database.Tables
{
	// One response packet of a ConversationNode, in send order (Sequence). FULLY STRUCTURED (#188) - the raw on-wire
	// Data blob is retired; the runtime constructs the packet entirely from typed columns: SubOpcode (the Knubot
	// dialogue IIR or the 0x2015 camera) + Env/Field/Flag scalars + SenderInstance + the decoded remoteformat
	// screen(s) as typed ConversationScreen rows (Screens) / CameraParams for the camera. The screen text is dialogue
	// content (its content key is the harvested, client-validated hash); the live player name/guild are regenerated
	// into the name/guildname parameters, and the npc/player handles re-addressed, at send time.
	public class ConversationPacket : BaseEntity
	{
		public uint ConversationNodeId { get; set; }

		[ForeignKey(nameof(ConversationNodeId))]
		public virtual ConversationNode Node { get; set; } = null!;

		// The decoded remoteformat dialogue screen(s), STRUCTURED (#188) - the runtime builds the wire XML from these
		// rows (ConversationScreenColumns.ToScreen -> RemoteFormatXml.Build). Empty for OpenChatWindow / non-Knubot.
		public virtual ICollection<ConversationScreen> Screens { get; set; } = [];

		public byte? Env { get; set; } // KnubotBaseIIR env byte
		public byte? Flag { get; set; } // OpenChatWindow/AppendText trailing flag (null otherwise)
		public byte[]? CameraParams { get; set; }

		public int Sequence { get; set; }

		// SetDialogueCamera (0x2015): the 10 camera param bytes after the two endpoint handles. Body =
		// [0xC350][npc][0xC350][player][CameraParams]. The sender instance is SenderInstance (shared with Knubot).
		public uint? Field { get; set; } // AppendText/CloseChatWindow scalar (null for Open/AnswerList)

		// The rpcHeader caller endpoint instance the packet was sent from - retail VARIES the GameAgent instance per
		// conversation (0x07..0x5a; 0x08 most common) and the client addresses its KnubotAnswer back to it, so it's
		// stored and re-framed at send time (was read from the raw Data blob, now retired).
		public uint? SenderInstance { get; set; }

		// --- structured screen (#188) ----------------------------------------------------------------------------
		// SubOpcode is the Knubot dialogue IIR (0x045B0B0F / 0x4A3D899A / 0xF164A4A5 / 0xC4AF39EB) OR the dialogue
		// camera (0x2015 DispatchSetDialogueCamera) - REQUIRED (the importer rejects a packet it can't decode).
		// Knubot uses Env/Field/Flag + the Screens rows; the camera uses CameraParams. Both use SenderInstance and
		// the live npc/player handles at send time.
		public uint? SubOpcode { get; set; } // #353: use an enum (layering-blocked - opcode enum lives in Core.Network)
	}
}
