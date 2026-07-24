using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One response packet of a ConversationNode, in send order (Sequence). FULLY STRUCTURED (#188) - the raw on-wire
	// Data blob is retired; the runtime constructs the packet entirely from typed columns: SubOpcode (the Knubot
	// dialogue IIR or the 0x2015 camera) + Env/Field/Flag scalars + SenderInstance + the decoded remoteformat
	// screen(s) as typed ConversationScreen rows (Screens) / the Shot* columns for the camera. The screen text is dialogue
	// content (its content key is the harvested, client-validated hash); the live player name/guild are regenerated
	// into the name/guildname parameters, and the npc/player handles re-addressed, at send time.
	//
	// Keyed by (node, Sequence): its position in the node's send order IS its identity, so a re-import reproduces the
	// same key instead of renumbering a surrogate and churning faolan.sql (see ConversationNode). The key widens by
	// one column per level down the tree, which is why only the ROOT needs a derived id.
	[PrimaryKey(nameof(ConversationNodeId), nameof(Sequence))]
	public class ConversationPacket
	{
		public uint ConversationNodeId { get; set; }

		[ForeignKey(nameof(ConversationNodeId))]
		public virtual ConversationNode Node { get; set; } = null!;

		public int Sequence { get; set; }

		// The decoded remoteformat dialogue screen(s), STRUCTURED (#188) - the runtime builds the wire XML from these
		// rows (ConversationScreenColumns.ToScreen -> RemoteFormatXml.Build). Empty for OpenChatWindow / non-Knubot.
		public virtual ICollection<ConversationScreen> Screens { get; set; } = [];

		// --- structured screen (#188) ----------------------------------------------------------------------------
		// SubOpcode is the Knubot dialogue IIR (0x045B0B0F / 0x4A3D899A / 0xF164A4A5 / 0xC4AF39EB) OR the dialogue
		// camera (0x2015 DispatchSetDialogueCamera) - REQUIRED (the importer rejects a packet it can't decode).
		// Knubot uses Env/Field/Flag + the Screens rows; the camera uses the Shot*/Autoface/HeadPaddingY columns. Both use SenderInstance and
		// the live npc/player handles at send time.
		public ConversationSubOpcode? SubOpcode { get; set; }

		public byte? Env { get; set; } // KnubotBaseIIR env byte
		public byte? Flag { get; set; } // OpenChatWindow/AppendText trailing flag (null otherwise)

		// SetDialogueCamera (0x2015): the camera arguments after the two endpoint handles. Body =
		// [0xC350][npc][0xC350][player][camera args]. The sender instance is SenderInstance (shared with Knubot).
		// COLUMNIZED (#410) from the 10-byte CameraParams blob, named after the client's own signature
		// DispatchSetDialogueCamera(i_cTargetID, i_cOther, nShotSize, nShotType, nShotEmphasis, nShotFov, nShotTilt,
		// bAutoface, i_vHeadPaddingY) - five bytes, a bool byte, then a big-endian float, exactly 10. Null on every
		// non-camera (Knubot) packet. Observed across the corpus: size 3/5, type 1/4, emphasis 1, fov 25, tilt
		// 0/5/10, autoface 0/1, headPaddingY 0.
		public byte? ShotSize { get; set; }
		public byte? ShotType { get; set; }
		public byte? ShotEmphasis { get; set; }
		public byte? ShotFov { get; set; }
		public byte? ShotTilt { get; set; }
		public bool? Autoface { get; set; }
		public float? HeadPaddingY { get; set; }

		public uint? Field { get; set; } // AppendText/CloseChatWindow scalar (null for Open/AnswerList)

		// The rpcHeader caller endpoint instance the packet was sent from - retail VARIES the GameAgent instance per
		// conversation (0x07..0x5a; 0x08 most common) and the client addresses its KnubotAnswer back to it, so it's
		// stored and re-framed at send time (was read from the raw Data blob, now retired).
		public uint? SenderInstance { get; set; }

		public override string ToString()
		{
			return $"{ConversationNodeId}#{Sequence} [{nameof(ConversationPacket)}] {SubOpcode}";
		}
	}
}
