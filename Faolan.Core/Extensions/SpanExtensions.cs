using System;
using System.Buffers.Binary;

namespace Faolan.Core.Extensions
{
	// Offset-addressed reads/writes of the wire's fixed-width primitives, so a field access reads as ONE operation
	// (`body.ReadU32Be(4)`) instead of a BinaryPrimitives call wrapped around a hand-sliced span. This is the shared
	// home for what was previously re-declared as a local `U32`/`ReadLE`/`WriteLe` helper in a dozen files.
	//
	// Every overload slices with the field's exact width, so a short buffer throws at the slice (naming the offset)
	// rather than reading past the field or silently succeeding on a truncated tail.
	//
	// `byte[]` and `Span<byte>` receivers bind to the ReadOnlySpan overloads directly (C# 13 first-class span
	// conversions) - no `.AsSpan()` at the call site.
	//
	// The set is deliberately COMPLETE (both endiannesses of every width BinaryPrimitives offers) rather than trimmed
	// to what is called today: a gap would send the next call site back to a raw BinaryPrimitives + hand-sliced span,
	// which is exactly the second idiom this class exists to retire. A few overloads therefore have no caller yet.
	//
	// Endianness follows the wire, and the two are NOT interchangeable: the Conan protocol is big-endian, EXCEPT
	// protobuf fixed32 floats (little-endian) and the client's own RDB/asset files (little-endian). See the
	// `protocol_float_endianness` note - both spellings in this codebase are intentional.
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
