using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.Economy
{
	/// <summary>
	///     <c>DispatchAddItem</c> - one item pushed into a container live:
	///     <c>[u32 containerKind][u32 charId][u32 itemPos][u32 entryLen][ProtoInventoryGameEntry][u32 feedback]</c>.
	/// </summary>
	/// <remarks>
	///     The entry is the same <c>ProtoInventoryGameEntry</c> the FullChar snapshot, shop window, AH grid and mail
	///     attachment carry, so one item shape serves them all. <c>feedback</c> drives the client's pickup notice.
	///     Byte-exact in <c>DispatchAddItemTests</c>.
	/// </remarks>
	public static class AddItemCodec
	{
		public const uint Method = (uint)GameCharInterfaceMethods.DispatchAddItem;

		// Canonical: the only method here that writes bytes.
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

	/// <summary>The DispatchAddItem body.</summary>
	public sealed record AddItem(ContainerKind Container, uint CharId, uint ItemPos, ProtoInventoryGameEntry Entry, uint Feedback = 0);
}
