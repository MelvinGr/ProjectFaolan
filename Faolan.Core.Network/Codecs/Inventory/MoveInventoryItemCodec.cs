using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
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
	// PARSE-ONLY, for the same reason as its sibling: we never emit it.
	//
	// The sibling DeleteItemIIR ("DropItem") shares this carrier framing but carries a DIFFERENT payload -
	// ProtoDeleteItemIIR, three fields - and has its own DeleteItemCodec. It used to be parsed here as a move;
	// see that codec for why the two alias on the wire and how the client settled it.
	public static class MoveInventoryItemCodec
	{
		public const uint Opcode = (uint)IirTypes.MoveInventoryItemIIR;

		public static MoveInventoryItemIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(MoveInventoryItemCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static MoveInventoryItemIIR Read(ref SpanReader r)
		{
			var entityClass = r.ReadU32Be();
			var charInstance = r.ReadU32Be();
			var reserved = r.ReadU32Be();

			return new MoveInventoryItemIIR(entityClass, charInstance, reserved, r.ReadProtobufByteLength<ProtoMoveInventoryItemIIR>());
		}
	}

	// EntityClass / Reserved are carried, not asserted - see DeleteItemIIR.
	public sealed record MoveInventoryItemIIR(uint EntityClass, uint CharInstance, uint Reserved, ProtoMoveInventoryItemIIR Move);
}
