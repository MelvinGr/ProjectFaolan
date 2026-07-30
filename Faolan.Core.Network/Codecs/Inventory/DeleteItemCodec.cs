using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.Inventory
{
	// DeleteItemIIR (0x72491DE9, the client's "DropItem" action) - the c2s drop. Same carrier framing as the sibling
	// MoveInventoryItemIIR, but a DIFFERENT payload message:
	//   [u32 0xC350][u32 charInstance][u32 reserved][protobuf ProtoDeleteItemIIR]
	// ProtoDeleteItemIIR = { container_id (ProtoID32), item_pos (int32), template_id (ProtoID32) } - the dropped
	// slot plus the template that was in it. No s2c reply; the server persists the removal and pushes the live
	// DispatchRemoveItem itself (the drop has no implicit client-side removal, #230).
	//
	// PARSE-ONLY: a c2s message we never emit, so there is no Build. (One would have to re-encode the protobuf
	// payload, which is not byte-stable across encoders - see the protobuf re-encode oracle, #385.)
	//
	// ⚠️ This used to be parsed as ProtoMoveInventoryItemIIR, on the claim that "despite the name it carries the
	// same body". It does not - and the mistake was invisible because the two messages ALIAS on the wire:
	//   ProtoMoveInventoryItemIIR { 1 ProtoID32 source_container_id, 2 int32 source_item_pos,
	//                               3 ProtoID32 destination_container_id, 4 int32 destination_item_pos }
	//   ProtoDeleteItemIIR        { 1 ProtoID32 container_id,        2 int32 item_pos,
	//                               3 ProtoID32 template_id }
	// Fields 1-3 have identical types, so a delete decodes into the move message WITHOUT error and silently files
	// the item TEMPLATE id into DestinationContainerId. The old handler only read fields 1-2, so it behaved
	// correctly by luck; anything reading the destination on the drop path would have read a template id as a
	// container kind.
	//
	// The captures cannot settle it (the corpus contains ZERO DeleteItemIIR packets), so the CLIENT is the oracle -
	// its DeleteItemIIR_t constructor (FUN_008d62f0 in AgeOfConan.exe) sets
	//     unaff_ESI[7] = ProtoDeleteItemIIR::vftable;
	// and fills exactly three fields, while the MoveInventoryItemIIR_t constructor (FUN_008d6090) sets
	// ProtoMoveInventoryItemIIR::vftable and fills a FOURTH (hasbit | 8). Recovered by
	// tools/ghidra/DumpDeleteItemIir.java + DumpDeleteItemFns.java -> tools/ghidra/output/delete_item_fns.txt.
	public static class DeleteItemCodec
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.DeleteItemIIR;

		public static DeleteItemIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(DeleteItemCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static DeleteItemIIR Read(ref SpanReader r)
		{
			var entityClass = r.ReadU32Be();
			var charInstance = r.ReadU32Be();
			var reserved = r.ReadU32Be();

			return new DeleteItemIIR(entityClass, charInstance, reserved, r.ReadProtobufByteLength<ProtoDeleteItemIIR>());
		}
	}

	// EntityClass is EntityClass.Character in every observed frame and Reserved is 0; both are CARRIED rather than
	// asserted, because the carrier is generic - what class an inbound drop names is the handler's business, not
	// the reader's.
	public sealed record DeleteItemIIR(uint EntityClass, uint CharInstance, uint Reserved, ProtoDeleteItemIIR Delete);
}
