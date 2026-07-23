namespace Faolan.Core.Enums
{
	// The wire sub-opcode a conversation response packet carries (Gitea #353 / #188): either one of the four Knubot
	// dialogue-screen IIRs (GameServer 0x2000 sub-opcodes) or the DispatchSetDialogueCamera RPC opcode (0x2015). The
	// importer requires exactly one of these (it rejects a packet it can't decode) and the runtime builds the packet
	// from it. Lives here in the shared base (Faolan.Core) so BOTH the wire decoder (Faolan.Core.Network,
	// ConversationScreenDecoder) and the persisted column (Faolan.Core.Database, ConversationPacket.SubOpcode) name the
	// same values without either layer depending on the other's opcode enums - the raw values duplicate
	// GameServer0X2000Opcodes.Knubot* + GameCharInterfaceOpcodes.DispatchSetDialogueCamera, which straddle two
	// Network-only enums, so there was no single existing enum to relocate. Backed by uint -> EF stores it as the same
	// INTEGER column, no migration.
	public enum ConversationSubOpcode : uint
	{
		KnubotOpenChatWindow = 0x045B0B0F,
		KnubotAppendText = 0x4A3D899A,
		KnubotAnswerList = 0xF164A4A5,
		KnubotCloseChatWindow = 0xC4AF39EB,
		SetDialogueCamera = 0x2015
	}
}
