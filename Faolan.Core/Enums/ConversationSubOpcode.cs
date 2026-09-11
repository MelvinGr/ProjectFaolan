namespace Faolan.Core.Enums
{
	// The wire sub-opcode a conversation response packet carries: either one of the four Knubot dialogue-screen IIRs
	// (GameServer 0x2000 sub-opcodes) or the DispatchSetDialogueCamera RPC opcode (0x2015). The importer requires
	// exactly one of these (it rejects a packet it cannot decode) and the runtime builds the packet from it. The five
	// values straddle two opcode enums (four in IirTypes + the camera in GameCharInterfaceMethods), so this names
	// them directly rather than duplicating the hex. Backed by uint, so EF stores it as a plain INTEGER column.
	public enum ConversationSubOpcode : uint
	{
		KnubotOpenChatWindow = IirTypes.KnubotOpenChatWindowIIR,
		KnubotAppendText = IirTypes.KnubotAppendTextIIR,
		KnubotAnswerList = IirTypes.KnubotAnswerListIIR,
		KnubotCloseChatWindow = IirTypes.KnubotCloseChatWindowIIR,
		SetDialogueCamera = GameCharInterfaceMethods.DispatchSetDialogueCamera
	}
}
