using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.Inventory
{
	/// <summary>
	///     MoveInventoryItemIIR - the c2s drag within or between containers:
	///     <c>[u32 entityClass][u32 charInstance][u32 reserved][protobuf ProtoMoveInventoryItemIIR]</c>.
	/// </summary>
	/// <remarks>
	///     <c>ProtoMoveInventoryItemIIR = { source_container_id, source_item_pos, destination_container_id,
	///     destination_item_pos }</c>, where each id is a <c>ProtoID32 { type, instance }</c>. All 18 captured frames
	///     carry all four fields. There is no s2c reply: the server persists the slot change and, for a cross-container
	///     or bank move, pushes the relevant add/remove itself.
	///     <para>
	///         Parse-only, for the same reason as its sibling: the server never emits it. The sibling DeleteItemIIR
	///         ("DropItem") shares this carrier framing but carries a different, three-field payload and has its own
	///         <see cref="DeleteItemCodec" />, whose remarks explain why the two alias on the wire.
	///     </para>
	/// </remarks>
	public static class MoveInventoryItemCodec
	{
		public const uint Opcode = (uint)IirTypes.MoveInventoryItemIIR;

		// This body starts at the entity class rather than at the sub-opcode: it is c2s, and the opcode above is what
		// the RPC frame carries, not the first word of the record. A slice that still carries the opcode reads four
		// bytes out of step and returns plausible values instead of failing. Declared, because CodecSurfaceConventionTests
		// reads this flag to know the body does not open with its opcode word.
		public const bool OpcodeIsInTheFrame = true;

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

	/// <remarks><c>EntityClass</c> and <c>Reserved</c> are carried rather than asserted; see <c>DeleteItemIIR</c>.</remarks>
	public sealed record MoveInventoryItemIIR(uint EntityClass, uint CharInstance, uint Reserved, ProtoMoveInventoryItemIIR Move);
}
