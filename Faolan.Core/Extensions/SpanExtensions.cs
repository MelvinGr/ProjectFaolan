using System;
using System.Buffers.Binary;

namespace Faolan.Core.Extensions
{
	/// <summary>
	///     Offset-addressed reads and writes of the wire's fixed-width primitives, so a field access reads as one
	///     operation (<c>body.ReadU32Be(4)</c>) instead of a <see cref="BinaryPrimitives" /> call wrapped around a
	///     hand-sliced span. This is the one home for that idiom; a codec never declares its own.
	/// </summary>
	/// <remarks>
	///     Every overload slices with the field's exact width, so a short buffer throws at the slice (naming the
	///     offset) rather than reading past the field or silently succeeding on a truncated tail.
	///     <para />
	///     <c>byte[]</c> and <c>Span&lt;byte&gt;</c> receivers bind to the <see cref="ReadOnlySpan{T}" /> overloads
	///     directly (first-class span conversions), so there is no <c>.AsSpan()</c> at the call site.
	///     <para />
	///     The set is deliberately complete - both endiannesses of every width <see cref="BinaryPrimitives" /> offers -
	///     rather than trimmed to what is called today: a gap would send the next call site back to a raw
	///     <see cref="BinaryPrimitives" /> call over a hand-sliced span, which is the idiom this class exists to
	///     retire. A few overloads therefore have no caller yet.
	///     <para />
	///     Endianness follows the wire, and the two are not interchangeable: the Conan protocol is big-endian, except
	///     protobuf fixed32 floats (little-endian) and the client's own RDB/asset files (little-endian). Both
	///     spellings in this codebase are intentional.
	/// </remarks>
	public static class SpanExtensions
	{
		extension(ReadOnlySpan<byte> span)
		{
			public short ReadI16Be(int offset) => BinaryPrimitives.ReadInt16BigEndian(span.Slice(offset, 2));
			public short ReadI16Le(int offset) => BinaryPrimitives.ReadInt16LittleEndian(span.Slice(offset, 2));

			public int ReadI32Be(int offset) => BinaryPrimitives.ReadInt32BigEndian(span.Slice(offset, 4));
			public int ReadI32Le(int offset) => BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset, 4));

			public ushort ReadU16Be(int offset) => BinaryPrimitives.ReadUInt16BigEndian(span.Slice(offset, 2));
			public ushort ReadU16Le(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(offset, 2));

			public uint ReadU32Be(int offset) => BinaryPrimitives.ReadUInt32BigEndian(span.Slice(offset, 4));
			public uint ReadU32Le(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset, 4));

			public ulong ReadU64Be(int offset) => BinaryPrimitives.ReadUInt64BigEndian(span.Slice(offset, 8));
			public ulong ReadU64Le(int offset) => BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(offset, 8));

			public float ReadF32Be(int offset) => BinaryPrimitives.ReadSingleBigEndian(span.Slice(offset, 4));
			public float ReadF32Le(int offset) => BinaryPrimitives.ReadSingleLittleEndian(span.Slice(offset, 4));

			public double ReadF64Be(int offset) => BinaryPrimitives.ReadDoubleBigEndian(span.Slice(offset, 8));
			public double ReadF64Le(int offset) => BinaryPrimitives.ReadDoubleLittleEndian(span.Slice(offset, 8));
		}

		extension(Span<byte> span)
		{
			public void WriteI16Be(int offset, short value) => BinaryPrimitives.WriteInt16BigEndian(span.Slice(offset, 2), value);
			public void WriteI16Le(int offset, short value) => BinaryPrimitives.WriteInt16LittleEndian(span.Slice(offset, 2), value);

			public void WriteI32Be(int offset, int value) => BinaryPrimitives.WriteInt32BigEndian(span.Slice(offset, 4), value);
			public void WriteI32Le(int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, 4), value);

			public void WriteU16Be(int offset, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(span.Slice(offset, 2), value);
			public void WriteU16Le(int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(offset, 2), value);

			public void WriteU32Be(int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(span.Slice(offset, 4), value);
			public void WriteU32Le(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(offset, 4), value);

			public void WriteU64Be(int offset, ulong value) => BinaryPrimitives.WriteUInt64BigEndian(span.Slice(offset, 8), value);
			public void WriteU64Le(int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(offset, 8), value);

			public void WriteF32Be(int offset, float value) => BinaryPrimitives.WriteSingleBigEndian(span.Slice(offset, 4), value);
			public void WriteF32Le(int offset, float value) => BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset, 4), value);

			public void WriteF64Be(int offset, double value) => BinaryPrimitives.WriteDoubleBigEndian(span.Slice(offset, 8), value);
			public void WriteF64Le(int offset, double value) => BinaryPrimitives.WriteDoubleLittleEndian(span.Slice(offset, 8), value);
		}
	}
}
