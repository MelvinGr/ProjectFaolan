using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.Economy
{
	// DispatchAddItem (s2c 0x2020) - one item pushed into a container live:
	//   [u32 containerKind][u32 charId][u32 itemPos][u32 entryLen][ProtoInventoryGameEntry][u32 feedback]
	// The entry is the SAME `ProtoInventoryGameEntry` the FullChar snapshot / shop window / AH grid / mail attachment
	// carry, so one item shape serves them all. `feedback` drives the client's pickup notice.
	//
	// Byte-exact in DispatchAddItemTests. It was `AddItemBody.WriteAddItemBody(FaolanStream, …)` - writing into the
	// LISTENER's stream, which is why Economy could not reach rule 1 until the parent sends moved (#530 wave 7).
	public static class AddItemCodec
	{
		public const ushort Opcode = (ushort)GameCharInterfaceOpcodes.DispatchAddItem;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(AddItem value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		public static AddItem Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(AddItemCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static AddItem Read(ref SpanReader r)
		{
			var container = (ContainerKind)r.ReadU32Be();
			var charId = r.ReadU32Be();
			var itemPos = r.ReadU32Be();
			var entry = r.ReadProtobufU32Length<ProtoInventoryGameEntry>();

			return new AddItem(container, charId, itemPos, entry, r.ReadU32Be());
		}

		internal static void Write(FaolanStream s, AddItem v)
		{
			s.WriteUInt32(v.Container).WriteUInt32(v.CharId).WriteUInt32(v.ItemPos).WriteArrayUInt32Length(v.Entry.ToProtobuf()).WriteUInt32(v.Feedback);
		}
	}

	// DispatchAddItem (0x2020).
	public sealed record AddItem(ContainerKind Container, uint CharId, uint ItemPos, ProtoInventoryGameEntry Entry, uint Feedback = 0);
}
