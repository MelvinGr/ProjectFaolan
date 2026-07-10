using Faolan.Core.Network.Constants;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.Inventory
{
	// The two c2s inventory-slot IIRs share ONE framing, so one codec parses both: MoveInventoryItemIIR (0x...,
	// drag within/between containers) and DeleteItemIIR (0x..., the drop-item action - despite the name it carries
	// the same ProtoMoveInventoryItemIIR body, source=the dropped slot). Wire:
	//   [u32 0xC350][u32 charInstance][u32 reserved][protobuf ProtoMoveInventoryItemIIR]
	// ProtoMoveInventoryItemIIR = { source_container_id, source_item_pos, destination_container_id,
	// destination_item_pos } where each id is a ProtoID32 { type, instance }. Neither message gets an s2c reply;
	// the server persists the slot change and (for a cross-container/bank move or a live drop) pushes the relevant
	// add/remove itself. See GameServerListener.Inbound (the MoveInventoryItemIIR / DeleteItemIIR cases).
	public static class InventoryMoveCodec
	{
		public static readonly GameServer0X2000Opcodes[] HandledTypes =
		[
			GameServer0X2000Opcodes.MoveInventoryItemIIR,
			GameServer0X2000Opcodes.DeleteItemIIR
		];

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
