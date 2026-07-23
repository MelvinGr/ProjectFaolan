namespace Faolan.Core.Enums
{
	// The wire sub-opcode a conversation response packet carries (Gitea #353 / #188): either one of the four Knubot
	// dialogue-screen IIRs (GameServer 0x2000 sub-opcodes) or the DispatchSetDialogueCamera RPC opcode (0x2015). The
	// importer requires exactly one of these (it rejects a packet it can't decode) and the runtime builds the packet
	// from it. The five values straddle TWO opcode enums (four in GameServer0X2000Opcodes + the camera in
	// GameCharInterfaceOpcodes), so there was no single existing enum to reuse - but since the opcode enums now live
	// here in the shared base too, this names them directly (single source of truth, no duplicated hex). Backed by
	// uint -> EF stores it as the same INTEGER column as the raw uint it replaced, no migration.
	public enum ConversationSubOpcode : uint
	{
		KnubotOpenChatWindow = (uint)GameServer0X2000Opcodes.KnubotOpenChatWindowIIR,
		KnubotAppendText = (uint)GameServer0X2000Opcodes.KnubotAppendTextIIR,
		KnubotAnswerList = (uint)GameServer0X2000Opcodes.KnubotAnswerListIIR,
		KnubotCloseChatWindow = (uint)GameServer0X2000Opcodes.KnubotCloseChatWindowIIR,
		SetDialogueCamera = (uint)GameCharInterfaceOpcodes.DispatchSetDialogueCamera
	}
}
