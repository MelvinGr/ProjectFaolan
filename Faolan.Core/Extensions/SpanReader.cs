using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using ProtoBuf;

namespace Faolan.Core.Extensions
{
	// The SEQUENTIAL twin of SpanExtensions: same primitive set, same names, but the offset is held by the reader
	// instead of written at the call site. `body.ReadU32Be(13)` becomes `r.ReadU32Be()` - the 13 stops existing.
	//
	// That is the entire point, and it is a correctness property rather than a style one. A codec that reads at fixed
	// offsets has NOT decoded the bytes before those offsets, so it cannot tell a correct body from one that merely
	// happens to carry the right word at 13; the offset is a claim about a grammar nobody wrote down. Reading
	// sequentially and finishing with EnsureAtEnd() makes the grammar total: every byte is either consumed by a
	// modelled field or the parse fails and says how many were left. See MagicBuffCodec's long form for the case this
	// was built for - three fields read at offsets, 60-odd bytes never looked at, and no way for a test to notice.
	//
	// Bounds are the reader's job, not each codec's: every read that would run past the end throws
	// CodecParseException naming the offset, the width it wanted and what was left. So a codec never needs a
	// defensive length check, and `if (body.Length < 26) return false;` stops being a thing codecs write.
	//
	// Endianness follows the wire and the two spellings are NOT interchangeable: Conan is big-endian, EXCEPT protobuf
	// fixed32 floats and the client's own RDB/asset files, which are little-endian (see `protocol_float_endianness`).
	// Both are offered at every width, deliberately - a missing overload is what sends the next call site back to a
	// hand-sliced BinaryPrimitives call.
	//
	// ReadListCount() is here because `ListFrame` moved down into this namespace alongside it. That matters for
	// diagnostics rather than tidiness: ListFrame.CountOf on its own can only say "0x1234 is not a framing word",
	// while the reader knows WHERE it read it - and the count-framed list is the protocol's most trap-laden
	// primitive (see ListFrame's own header for the three subsystems that shipped a 0x7E2 "constant").
	public ref struct SpanReader
	{
		private readonly ReadOnlySpan<byte> _data;

		// Optional, for the exception message only. Pass nameof(TheCodec) from Parse; the stack trace names it too.
		private readonly string? _codec;

		private int _position;

		public SpanReader(ReadOnlySpan<byte> data, string? codec = null)
		{
			_data = data;
			_codec = codec;
			_position = 0;
		}

		// True when every byte has been consumed. EnsureAtEnd() is the assertion form and is what Parse calls.
		public readonly bool AtEnd => _position >= _data.Length;

		public readonly int Length => _data.Length;

		public readonly int Position => _position;

		public readonly int Remaining => _data.Length - _position;

		// ---- framing -----------------------------------------------------------------------------------------

		// The whole-body assertion that makes rule 3 mechanical. Called at the end of every Parse: if the grammar
		// stopped early, this is what turns a silent partial decode into a failure that names the leftover bytes.
		public readonly void EnsureAtEnd()
		{
			if (AtEnd)
				return;

			throw CodecParseException.Trailing(_codec, _position, Remaining, Preview());
		}

		// Asserts the next big-endian u32 equals `expected` and consumes it. The IIR/type word at the head of a body
		// is the usual caller: reading it and checking it are one operation, so a codec cannot forget the check.
		public uint ExpectU32Be(uint expected, string? what = null)
		{
			var offset = _position;
			var actual = ReadU32Be();
			if (actual != expected)
				throw CodecParseException.Unexpected(_codec, offset, $"{what ?? "word"} 0x{expected:X8}", $"0x{actual:X8}");

			return actual;
		}

		// The same assertion at the other widths. Constant lead flags, reserved words and version words are just as
		// much part of a grammar as the type word, and a codec that merely SKIPS them cannot tell a correct body from
		// one carrying something else there.
		public ushort ExpectU16Be(ushort expected, string? what = null)
		{
			var offset = _position;
			var actual = ReadU16Be();
			if (actual != expected)
				throw CodecParseException.Unexpected(_codec, offset, $"{what ?? "word"} 0x{expected:X4}", $"0x{actual:X4}");

			return actual;
		}

		public ulong ExpectU64Be(ulong expected, string? what = null)
		{
			var offset = _position;
			var actual = ReadU64Be();
			if (actual != expected)
				throw CodecParseException.Unexpected(_codec, offset, $"{what ?? "word"} 0x{expected:X16}", $"0x{actual:X16}");

			return actual;
		}

		public byte ExpectU8(byte expected, string? what = null)
		{
			var offset = _position;
			var actual = ReadU8();
			if (actual != expected)
				throw CodecParseException.Unexpected(_codec, offset, $"{what ?? "byte"} 0x{expected:X2}", $"0x{actual:X2}");

			return actual;
		}

		// The remaining bytes WITHOUT consuming them - for discriminating a message family before committing to a
		// shape. Prefer ExpectU32Be/PeekU32Be; this is the escape hatch.
		public readonly ReadOnlySpan<byte> PeekRemaining()
		{
			return _data[_position..];
		}

		// The next big-endian u32 without consuming it. Used by family codecs (rule 5) to pick the concrete record
		// before parsing it.
		public readonly uint PeekU32Be()
		{
			return Peek(4).ReadU32Be(0);
		}

		public void Skip(int count)
		{
			Take(count);
		}

		// The element count of a count-framed list: a u32 == (count + 1) * 0x3F1, which the client's own readers
		// divide rather than compare. A word that is not a valid multiple is a decode failure, reported WITH the
		// offset - the thing ListFrame.CountOf cannot say on its own.
		//
		// ⚠️ Reading this as a constant is the single most repeated mistake in this protocol: 0x7E2 is a ONE-element
		// list and 0x3F1 is an EMPTY one, and both have shipped as "magic terminators". If a grammar has a constant
		// where a list could be, it is probably this. See ListFrame.
		public int ReadListCount()
		{
			var offset = _position;
			var word = ReadU32Be();
			if (!ListFrame.TryCountOf(word, out var count))
				throw CodecParseException.Unexpected(_codec, offset, $"a count-framed list word (a non-zero multiple of 0x{ListFrame.Unit:X})", $"0x{word:X8}");

			return count;
		}

		// ---- byte blocks -------------------------------------------------------------------------------------

		public ReadOnlySpan<byte> ReadBytes(int count)
		{
			return Take(count);
		}

		public ReadOnlySpan<byte> ReadBytesU8Length()
		{
			return Take(ReadU8());
		}

		public ReadOnlySpan<byte> ReadBytesU16Length()
		{
			return Take(ReadU16Be());
		}

		public ReadOnlySpan<byte> ReadBytesU32Length()
		{
			return Take(checked((int)ReadU32Be()));
		}

		// Everything left. A codec that calls this is declaring the tail opaque - legitimate for a not-yet-decoded
		// region, but it also satisfies EnsureAtEnd() trivially, so it must be a deliberate, commented choice.
		public ReadOnlySpan<byte> ReadRemaining()
		{
			return Take(Remaining);
		}

		// ---- fixed-width primitives (mirrors SpanExtensions) --------------------------------------------------

		public double ReadF64Be() => BinaryPrimitives.ReadDoubleBigEndian(Take(8));
		public double ReadF64Le() => BinaryPrimitives.ReadDoubleLittleEndian(Take(8));

		public float ReadF32Be() => BinaryPrimitives.ReadSingleBigEndian(Take(4));
		public float ReadF32Le() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));

		public short ReadI16Be() => BinaryPrimitives.ReadInt16BigEndian(Take(2));
		public short ReadI16Le() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));

		public int ReadI32Be() => BinaryPrimitives.ReadInt32BigEndian(Take(4));
		public int ReadI32Le() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

		public long ReadI64Be() => BinaryPrimitives.ReadInt64BigEndian(Take(8));
		public long ReadI64Le() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));

		public sbyte ReadI8() => (sbyte)Take(1)[0];

		public ushort ReadU16Be() => BinaryPrimitives.ReadUInt16BigEndian(Take(2));
		public ushort ReadU16Le() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

		public uint ReadU32Be() => BinaryPrimitives.ReadUInt32BigEndian(Take(4));
		public uint ReadU32Le() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

		public ulong ReadU64Be() => BinaryPrimitives.ReadUInt64BigEndian(Take(8));
		public ulong ReadU64Le() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));

		public byte ReadU8() => Take(1)[0];

		// ---- composite wire types ----------------------------------------------------------------------------

		public Quaternion ReadQuaternionBe()
		{
			return new Quaternion(ReadF32Be(), ReadF32Be(), ReadF32Be(), ReadF32Be());
		}

		// WIRE STRINGS ARE RAW BYTES - Latin1, never ASCII or UTF-8. The client's BinaryStream::ReadString memcpy's
		// the length-prefixed bytes straight into a std::string without transcoding, so Latin1 (a bijection over
		// 0x00-0xFF) is the only mapping that round-trips. See FaolanStream.ReadString for the full note.
		public string ReadStringU16Length()
		{
			return Encoding.Latin1.GetString(ReadBytesU16Length());
		}

		public string ReadStringU32Length()
		{
			return Encoding.Latin1.GetString(ReadBytesU32Length());
		}

		public string ReadStringU8Length()
		{
			return Encoding.Latin1.GetString(ReadBytesU8Length());
		}

		// A length-prefixed protobuf message - the read counterpart to FaolanStream.WriteProtobufByteLength. The
		// prefix is a single BYTE, which is what the Conan carrier messages use around their protobuf payloads.
		public T ReadProtobufByteLength<T>() where T : IExtensible
		{
			return ReadBytesU8Length().FromProtobuf<T>();
		}

		// A base-128 varint (protobuf LEB128) - the read counterpart to FaolanStream.WriteVarint.
		public ulong ReadVarint()
		{
			var offset = _position;
			ulong result = 0;
			var shift = 0;
			while (true)
			{
				var b = ReadU8();
				result |= (ulong)(b & 0x7F) << shift;
				if ((b & 0x80) == 0)
					return result;

				shift += 7;
				if (shift > 63)
					throw CodecParseException.Unexpected(_codec, offset, "a varint of at most 10 bytes", "a longer one");
			}
		}

		public Vector3 ReadVector3Be()
		{
			return new Vector3(ReadF32Be(), ReadF32Be(), ReadF32Be());
		}

		// ---- internals ---------------------------------------------------------------------------------------

		private readonly string Preview()
		{
			var tail = _data[_position..];
			var shown = tail.Length > 16 ? tail[..16] : tail;

			return Convert.ToHexString(shown) + (tail.Length > shown.Length ? "..." : "");
		}

		// The single bounds check every read goes through, without advancing. Kept separate from Take so the peeking
		// members can be `readonly` - a readonly member calling a mutating one copies the whole struct (CS8656).
		private readonly ReadOnlySpan<byte> Peek(int count)
		{
			ArgumentOutOfRangeException.ThrowIfNegative(count);
			if (count > Remaining)
				throw CodecParseException.Underrun(_codec, _position, count, Remaining);

			return _data.Slice(_position, count);
		}

		private ReadOnlySpan<byte> Take(int count)
		{
			var slice = Peek(count);
			_position += count;

			return slice;
		}
	}
}
