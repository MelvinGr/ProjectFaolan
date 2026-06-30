using System.ComponentModel.DataAnnotations.Schema;

namespace Faolan.Core.Database.Tables
{
	// One response packet of a ConversationNode, in send order (Sequence). Data is the raw on-wire Conan packet
	// (kept as the fallback + the source the non-Knubot packets still replay from).
	//
	// For the four Knubot dialogue IIRs (#188) the screen is also stored STRUCTURED so the runtime constructs it
	// from fields instead of lifting/patching the raw blob: SubOpcode + Env + Field/Flag scalars + the decoded
	// remoteformat XML screen(s) (ScreensJson). When SubOpcode is null the packet is non-Knubot (or unpopulated) and
	// the runtime falls back to the Data path. The screen XML is the dialogue TEXT template (its content key is the
	// harvested, client-validated hash); the live player name/guild are regenerated into it at send time.
	public class ConversationPacket : BaseEntity
	{
		[ForeignKey(nameof(ConversationNodeId))]
		public virtual ConversationNode Node { get; set; }

		public byte[] Data { get; set; }

		public int Sequence { get; set; }
		public uint ConversationNodeId { get; set; }

		// --- structured screen (#188); null SubOpcode = unrecognised, use Data ----------------------------------
		// SubOpcode is the Knubot dialogue IIR (0x045B0B0F / 0x4A3D899A / 0xF164A4A5 / 0xC4AF39EB) OR the dialogue
		// camera (0x2015 DispatchSetDialogueCamera). Knubot uses Env/Field/Flag/ScreensJson; the camera uses
		// CameraAgentInstance + CameraParams. The npc/player handles are always the live ones at send time.
		public uint? SubOpcode { get; set; }
		public byte? Env { get; set; }             // KnubotBaseIIR env byte
		public uint? Field { get; set; }           // AppendText/CloseChatWindow scalar (null for Open/AnswerList)
		public byte? Flag { get; set; }            // OpenChatWindow/AppendText trailing flag (null otherwise)
		public string ScreensJson { get; set; }    // JSON array of the remoteformat XML screen(s); null for Open

		// SetDialogueCamera (0x2015): the GameCharAgent sender instance (varies per conversation) + the 10 camera
		// param bytes after the two endpoint handles. Body = [0xC350][npc][0xC350][player][CameraParams].
		public uint? CameraAgentInstance { get; set; }
		public byte[] CameraParams { get; set; }
	}
}
