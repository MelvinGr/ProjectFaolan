using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Economy
{
	/// <summary>
	///     <c>DispatchRemoveItem</c> - the inverse of <see cref="AddItemCodec" />:
	///     <c>[u32 containerKind][u32 charId][u32 itemPos][u32 amountToRemove]</c>. An <c>amountToRemove</c> of 0 removes
	///     the whole stack. Byte-exact in <c>DispatchRemoveItemTests</c>.
	/// </summary>
	public static class RemoveItemCodec
	{
		public const uint Method = (uint)GameCharInterfaceMethods.DispatchRemoveItem;

		// Canonical: the only method here that writes bytes.
		public static byte[] Build(RemoveItem value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		public static RemoveItem Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(RemoveItemCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static RemoveItem Read(ref SpanReader r)
		{
			return new RemoveItem((ContainerKind)r.ReadU32Be(), r.ReadU32Be(), r.ReadU32Be(), r.ReadU32Be());
		}

		internal static void Write(FaolanStream s, RemoveItem v)
		{
			s.WriteUInt32(v.Container).WriteUInt32(v.CharId).WriteUInt32(v.ItemPos).WriteUInt32(v.AmountToRemove);
		}
	}

	/// <summary>The DispatchRemoveItem body.</summary>
	public sealed record RemoveItem(ContainerKind Container, uint CharId, uint ItemPos, uint AmountToRemove = 0);
}
