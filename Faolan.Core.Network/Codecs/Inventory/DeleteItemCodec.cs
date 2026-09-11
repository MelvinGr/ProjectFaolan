using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.Inventory
{
	/// <summary>
	///     DeleteItemIIR (the client's "DropItem" action) - the c2s drop. Same carrier framing as the sibling
	///     <see cref="MoveInventoryItemCodec" />, but a different payload message:
	///     <c>[u32 entityClass][u32 charInstance][u32 reserved][protobuf ProtoDeleteItemIIR]</c>.
	/// </summary>
	/// <remarks>
	///     <c>ProtoDeleteItemIIR = { container_id (ProtoID32), item_pos (int32), template_id (ProtoID32) }</c> - the
	///     dropped slot plus the template that was in it. There is no s2c reply: the server persists the removal and
	///     pushes the live DispatchRemoveItem itself, since the drop has no implicit client-side removal.
	///     <para>
	///         Parse-only, because this is a c2s message the server never emits. Building one would mean re-encoding
	///         the protobuf payload, which is not byte-stable across encoders.
	///     </para>
	///     <para>
	///         It must not be parsed as <c>ProtoMoveInventoryItemIIR</c>, even though the two alias on the wire:
	///         <code>
	///         ProtoMoveInventoryItemIIR { 1 ProtoID32 source_container_id, 2 int32 source_item_pos,
	///                                     3 ProtoID32 destination_container_id, 4 int32 destination_item_pos }
	///         ProtoDeleteItemIIR        { 1 ProtoID32 container_id,        2 int32 item_pos,
	///                                     3 ProtoID32 template_id }
	///         </code>
	///         Fields 1-3 have identical types, so a delete decodes into the move message without error and silently
	///         files the item template id into <c>DestinationContainerId</c>.
	///     </para>
	///     <para>
	///         The corpus carries 15 of these (<c>--report codec-sweep</c>), all in NecroL80's captures, and they
	///         confirm the client-derived reading rather than merely permitting it: every one carries fields 1-3 and
	///         stops, while all 54 captured MoveInventoryItemIIR bodies carry a fourth. Protobuf would decode a move
	///         into this message without complaint, so the confirmation is the absence of that field rather than the
	///         parse succeeding. The client settles it too: its <c>DeleteItemIIR_t</c> constructor (FUN_008d62f0 in
	///         AgeOfConan.exe) sets the <c>ProtoDeleteItemIIR</c> vftable and fills exactly three fields, while
	///         <c>MoveInventoryItemIIR_t</c> (FUN_008d6090) sets the <c>ProtoMoveInventoryItemIIR</c> vftable and fills
	///         a fourth. Recovered by tools/ghidra/DumpDeleteItemIir.java and DumpDeleteItemFns.java.
	///     </para>
	/// </remarks>
	public static class DeleteItemCodec
	{
		public const uint Opcode = (uint)IirTypes.DeleteItemIIR;

		// This body starts at the entity class rather than at the sub-opcode: it is c2s, and the opcode above is what
		// the RPC frame carries, not the first word of the record. Declared rather than left to be inferred, because
		// handing a codec the wrong slice reads four bytes out of step and returns plausible values instead of
		// failing.
		public const bool OpcodeIsInTheFrame = true;

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

	/// <remarks>
	///     <c>EntityClass</c> is the character class in every observed frame and <c>Reserved</c> is 0; both are carried
	///     rather than asserted, because the carrier is generic - what class an inbound drop names is the handler's
	///     business, not the reader's.
	/// </remarks>
	public sealed record DeleteItemIIR(uint EntityClass, uint CharInstance, uint Reserved, ProtoDeleteItemIIR Delete);
}
