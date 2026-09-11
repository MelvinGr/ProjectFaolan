using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using ProtoBuf;

namespace Faolan.Core.Extensions
{
	/// <summary>
	///     The sequential twin of <see cref="SpanExtensions" />: same primitive set, same names, but the offset is
	///     held by the reader instead of written at the call site - <c>body.ReadU32Be(13)</c> becomes
	///     <c>r.ReadU32Be()</c> and the 13 stops existing.
	/// </summary>
	/// <remarks>
	///     That is a correctness property rather than a style one. A codec that reads at fixed offsets has not decoded
	///     the bytes before those offsets, so it cannot tell a correct body from one that merely happens to carry the
	///     right word at 13; the offset is a claim about a grammar nobody wrote down. Reading sequentially and
	///     finishing with <see cref="EnsureAtEnd" /> makes the grammar total: every byte is either consumed by a
	///     modelled field or the parse fails and says how many were left.
	///     <para />
	///     Bounds are the reader's job, not each codec's: every read that would run past the end throws
	///     <see cref="CodecParseException" /> naming the offset, the width it wanted and what was left. A codec
	///     therefore never writes a defensive length check of its own.
	///     <para />
	///     Endianness follows the wire and the two spellings are not interchangeable: Conan is big-endian, except
	///     protobuf fixed32 floats and the client's own RDB/asset files, which are little-endian. Both are offered at
	///     every width, deliberately - a missing overload is what sends the next call site back to a hand-sliced
	///     <c>BinaryPrimitives</c> call.
	///     <para />
	///     <see cref="ReadListCount" /> lives here, alongside <see cref="ListFrame" />, for diagnostics rather than
	///     tidiness: <see cref="ListFrame.CountOf" /> on its own can only say "0x1234 is not a framing word", while
	///     the reader knows where it read it - and the count-framed list is the protocol's most trap-laden primitive.
	/// </remarks>
	public ref struct SpanReader
	{
		private readonly ReadOnlySpan<byte> _data;

		/// <summary>
		///     Optional, for the exception message only. Pass <c>nameof(TheCodec)</c> from <c>Parse</c>; the stack
		///     trace names it too.
		/// </summary>
		private readonly string? _codec;

		private int _position;

		public SpanReader(ReadOnlySpan<byte> data, string? codec = null)
		{
			_data = data;
			_codec = codec;
			_position = 0;
		}

		/// <summary>True when every byte has been consumed. <see cref="EnsureAtEnd" /> is the assertion form.</summary>
		public readonly bool AtEnd => _position >= _data.Length;

		public readonly int Length => _data.Length;

		public readonly int Position => _position;

		public readonly int Remaining => _data.Length - _position;

		// ---- framing -----------------------------------------------------------------------------------------

		/// <summary>
		///     The whole-body assertion every <c>Parse</c> ends with: if the grammar stopped early, this is what turns
		///     a silent partial decode into a failure that names the leftover bytes.
		/// </summary>
		public readonly void EnsureAtEnd()
		{
			if (AtEnd)
				return;

			throw CodecParseException.Trailing(_codec, _position, Remaining, Preview());
		}

		/// <summary>
		///     Asserts the next big-endian u32 equals <paramref name="expected" /> and consumes it. The IIR/type word
		///     at the head of a body is the usual caller: reading it and checking it are one operation, so a codec
		///     cannot forget the check.
		/// </summary>
		public uint ExpectU32Be(uint expected, string? what = null)
		{
			var offset = _position;
			var actual = ReadU32Be();
			if (actual != expected)
				throw CodecParseException.Unexpected(_codec, offset, $"{what ?? "word"} 0x{expected:X8}", $"0x{actual:X8}");

			return actual;
		}

		/// <summary>
		///     The same assertion at the other widths. Constant lead flags, reserved words and version words are as
		///     much part of a grammar as the type word, and a codec that merely skips them cannot tell a correct body
		///     from one carrying something else there.
		/// </summary>
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

		/// <summary>
		///     The remaining bytes without consuming them - for discriminating a message family before committing to a
		///     shape. Prefer <see cref="ExpectU32Be" />/<see cref="PeekU32Be" />; this is the escape hatch.
		/// </summary>
		public readonly ReadOnlySpan<byte> PeekRemaining()
		{
			return _data[_position..];
		}

		/// <summary>
		///     The next big-endian u32 without consuming it. Used by family codecs to pick the concrete record before
		///     parsing it.
		/// </summary>
		public readonly uint PeekU32Be()
		{
			return Peek(4).ReadU32Be(0);
		}

		public void Skip(int count)
		{
			Take(count);
		}

		/// <summary>
		///     The element count of a count-framed list: a u32 equal to <c>(count + 1) * 0x3F1</c>, which the client's
		///     own readers divide rather than compare. A word that is not a valid multiple is a decode failure,
		///     reported with the offset - the thing <see cref="ListFrame.CountOf" /> cannot say on its own.
		/// </summary>
		/// <remarks>
		///     Reading this word as a constant is the most repeated mistake in this protocol: 0x7E2 is a one-element
		///     list and 0x3F1 an empty one, and both read as magic terminators. Where a grammar has a constant and a
		///     list could stand, it is probably this. See <see cref="ListFrame" />.
		/// </remarks>
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

		/// <summary>
		///     Everything left. A codec that calls this is declaring the tail opaque - legitimate for a not-yet-decoded
		///     region, but it also satisfies <see cref="EnsureAtEnd" /> trivially, so it must be a deliberate,
		///     commented choice.
		/// </summary>
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

		/// <summary>
		///     Wire strings are raw bytes - Latin1, never ASCII or UTF-8. The client's <c>BinaryStream::ReadString</c>
		///     copies the length-prefixed bytes straight into a <c>std::string</c> without transcoding, so Latin1 (a
		///     bijection over 0x00-0xFF) is the only mapping that round-trips. See <c>FaolanStream.ReadString</c> for
		///     the full note.
		/// </summary>
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

		/// <summary>
		///     A C-style NUL-terminated string. Rarer than the length-prefixed forms above and only used where the
		///     client itself terminates rather than counts - the <c>PFAccessWindow_</c> catalog's short codes are the
		///     live case.
		/// </summary>
		/// <exception cref="CodecParseException">
		///     The terminator is missing. An unterminated string means the grammar is wrong, not that the string runs
		///     to the end of the body.
		/// </exception>
		public string ReadStringNullTerminated()
		{
			var start = _position;
			while (_position < _data.Length)
			{
				if (_data[_position++] == 0)
					return Encoding.Latin1.GetString(_data[start..(_position - 1)]);
			}

			throw CodecParseException.Unexpected(_codec, start, "a NUL-terminated string", "no terminator before the end of the body");
		}

		/// <summary>
		///     A length-prefixed protobuf message - the read counterpart to
		///     <c>FaolanStream.WriteProtobufByteLength</c>. The prefix is a single byte, which is what the Conan
		///     carrier messages use around their protobuf payloads.
		/// </summary>
		public T ReadProtobufByteLength<T>() where T : IExtensible
		{
			return ReadBytesU8Length().FromProtobuf<T>();
		}

		/// <summary>
		///     A protobuf message behind a u32 big-endian length prefix - the read counterpart to
		///     <c>FaolanStream.WriteArrayUInt32Length(msg.ToProtobuf())</c>. Both prefix widths are real on the wire:
		///     the 0x2000 IIR carriers use u8 (above) while the RPC method bodies that embed an inventory entry use
		///     u32.
		/// </summary>
		public T ReadProtobufU32Length<T>() where T : IExtensible
		{
			return ReadBytesU32Length().FromProtobuf<T>();
		}

		/// <summary>A base-128 varint (protobuf LEB128) - the read counterpart to <c>FaolanStream.WriteVarint</c>.</summary>
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

		/// <summary>
		///     The single bounds check every read goes through, without advancing. Kept separate from <c>Take</c> so
		///     the peeking members can be <c>readonly</c> - a readonly member calling a mutating one copies the whole
		///     struct (CS8656).
		/// </summary>
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
