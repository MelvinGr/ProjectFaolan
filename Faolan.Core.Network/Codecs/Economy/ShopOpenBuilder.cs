using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Economy
{
	// The vendor trade-OPEN reply pair (#134), decoded from the NecroL80 sell_items_to_trader capture (frames
	// 1188-1193): the client opens a vendor by sending a TalkToNpc BehaviorControlCenterIIR Interact at the
	// vendor NPC, and retail answers with
	//   1. a LookAtIIR/SelectDeselect (0x0D155695) "trade select" echo binding the player to the vendor handle
	//      (frame 1189),
	//   2. the BCC echo-ack of the interact - the standard behavior echo WITHOUT the combat ack's 12-zero pad
	//      (frame 1193, first record; built via BehaviorAckBody.BuildBehaviorEchoAckBody padTail: false),
	//   3. DispatchShopData (0x2033) carrying the window content (frame 1193, second record).
	// The window is gated on this conversation: a cold DispatchShopData push never pops it in-client (#134's
	// premise, confirmed 2026-06-17). Byte-exact goldens: ShopOpenBuilderTests.
	public static class ShopOpenBuilder
	{
		// The IIR type this codec emits (#317 convention; read by the coverage matrix).
		public const uint Opcode = (uint)GameServer0X2000Opcodes.LookAtIIR;

		// Frame 1189: [subop 0x0D155695][C350][playerInstance][u8 0][C350][vendorHandle][u32 0][u32 2]. The
		// trailing 2 is the select MODE observed on the trade open (a plain click select carries different
		// trailers; only the trade-open form is built here).
		public static byte[] BuildTradeSelectEcho(uint playerInstance, uint vendorHandle)
		{
			return new FaolanStream()
				.WriteUInt32(GameServer0X2000Opcodes.LookAtIIR)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(playerInstance)
				.WriteByte(0)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(vendorHandle)
				.WriteUInt32(0)
				.WriteUInt32(2)
				.ToArray();
		}
	}
}
