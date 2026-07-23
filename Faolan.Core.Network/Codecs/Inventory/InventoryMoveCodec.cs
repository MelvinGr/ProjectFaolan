using Faolan.Core.Enums;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.Inventory
{
	// MoveInventoryItemIIR (0x2A8A1B49) - the c2s drag within/between containers. Wire:
	//   [u32 0xC350][u32 charInstance][u32 reserved][protobuf ProtoMoveInventoryItemIIR]
	// ProtoMoveInventoryItemIIR = { source_container_id, source_item_pos, destination_container_id,
	// destination_item_pos } where each id is a ProtoID32 { type, instance }. All 18 captured frames carry all
	// four fields. No s2c reply; the server persists the slot change and (for a cross-container/bank move) pushes
	// the relevant add/remove itself. See GameServerListener.Inbound (the MoveInventoryItemIIR case).
	//
	// The sibling DeleteItemIIR ("DropItem") shares this carrier framing but carries a DIFFERENT payload -
	// ProtoDeleteItemIIR, three fields - and has its own InventoryDeleteCodec. It used to be parsed here as a move;
	// see that codec for why the two alias on the wire and how the client settled it.
	public static class InventoryMoveCodec
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.MoveInventoryItemIIR;

		// Parse the [0xC350][charInstance][reserved][protobuf] framing, returning the character instance and the move
		// descriptor. `body` must be positioned at the IIR body start (just past the [len][subop] carrier prefix).
		public static (uint CharInstance, ProtoMoveInventoryItemIIR Move) Parse(FaolanStream body)
		{
			body.ReadUInt32(); // 0xC350 entity-class tag
			var charInstance = body.ReadUInt32();
			body.ReadUInt32(); // reserved
			var move = body.ReadProtobufByteLength<ProtoMoveInventoryItemIIR>();
			return (charInstance, move);
		}
	}
}
