using System;
using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Text;
using Faolan.Core.Extensions;
using Faolan.Core.Helpers;
using ProtoBuf;

namespace Faolan.Core
{
	public class FaolanStream : MemoryStream
	{
		public new uint Length => checked((uint)base.Length);

		public new uint Position
		{
			get => checked((uint)base.Position);
			set => base.Position = value;
		}

		public FaolanStream()
		{
		}

		public FaolanStream(byte[] data) : base(data)
		{
		}

		public FaolanStream(byte[] data, bool writable) : base(data, writable)
		{
		}

		// A read-only cursor over a WINDOW of an existing array (no copy) - used to read a message body that is a slice
		// of a larger buffer (e.g. Rpc.BodyReader over the inbound frame's array).
		public FaolanStream(byte[] data, int index, int count) : base(data, index, count, false, true)
		{
		}

		public byte[] ReadArray(uint length)
		{
			// Never allocate more than the buffer can supply: a hostile length-prefix (up to 0xFFFFFFFF) would
			// otherwise trigger a multi-GB allocation before ReadExactly fails - an OOM DoS. Checking against the
			// bytes actually remaining is safe because no legitimate read can exceed the packet.
			if (length > Length - Position)
				throw new EndOfStreamException($"ReadArray length {length} exceeds {Length - Position} byte(s) remaining");

			var buffer = new byte[length];
			ReadExactly(buffer);

			return buffer;
		}

		public byte[] ReadArrayByteLength()
		{
			return ReadArray(ReadByte());
		}

		public byte[] ReadArrayUInt16Length()
		{
			return ReadArray(ReadUInt16());
		}

		public byte[] ReadArrayUInt32Length()
		{
			return ReadArray(ReadUInt32());
		}

		public new byte ReadByte()
		{
			var ret = base.ReadByte();
			if (ret == -1)
				throw new EndOfStreamException("Attempted to read past the end of the stream.");

			return checked((byte)ret);
		}

		public T ReadByte<T>() where T : Enum
		{
			return (T)Enum.ToObject(typeof(T), ReadByte());
		}

		public float ReadFloat()
		{
			Span<byte> buffer = stackalloc byte[sizeof(float)];
			ReadExactly(buffer);

			return BinaryPrimitives.ReadSingleBigEndian(buffer);
		}

		public T ReadProtobufByteLength<T>() where T : IExtensible
		{
			var body = ReadArrayByteLength();
			return body.FromProtobuf<T>();
		}

		public T ReadProtobufUInt32Length<T>() where T : IExtensible
		{
			var body = ReadArrayUInt32Length();
			return body.FromProtobuf<T>();
		}

		public Quaternion ReadQuaternion()
		{
			return new Quaternion(ReadFloat(), ReadFloat(), ReadFloat(), ReadFloat());
		}

		public byte[] ReadRemainingArray()
		{
			return ReadArray(Length - Position);
		}

		// WIRE STRINGS ARE RAW BYTES - use Latin1, never ASCII or UTF-8.
		//
		// The client's BinaryStream::ReadString (FUN_00cbf840 <- FUN_0048f840) memcpy's the length-prefixed bytes
		// straight into an MSVC std::string. It does NOT transcode. So a wire string is a byte string, and the only
		// correct .NET mapping is Latin1: it is a bijection between bytes 0x00-0xFF and chars U+0000-U+00FF, so every
		// byte round-trips.
		//   - Encoding.ASCII is LOSSY both ways: it turns any byte >= 0x80 into '?' on read, and any non-ASCII char
		//     into '?' on write. It silently destroys accented characters.
		//   - Encoding.UTF8 re-encodes a high char into a multi-byte sequence the client never sent, and throws or
		//     substitutes on bytes that are not valid UTF-8 - which raw byte strings frequently are not.
		// (The captures cannot show this: every decodable captured string is pure ASCII, where all three agree - the
		// quest XML escapes non-ASCII as &#nn; entities. The client is the oracle here, not the corpus.)
		public string ReadString()
		{
			return Encoding.Latin1.GetString(ReadArrayUInt16Length());
		}

		public string ReadStringUInt32Length()
		{
			return Encoding.Latin1.GetString(ReadArrayUInt32Length());
		}

		public string ReadStringByteLength()
		{
			return Encoding.Latin1.GetString(ReadArrayByteLength());
		}

		public ushort ReadUInt16()
		{
			Span<byte> buffer = stackalloc byte[sizeof(ushort)];
			ReadExactly(buffer);

			return BinaryPrimitives.ReadUInt16BigEndian(buffer);
		}

		public T ReadUInt16<T>() where T : Enum
		{
			return (T)Enum.ToObject(typeof(T), ReadUInt16());
		}

		public uint ReadUInt32()
		{
			Span<byte> buffer = stackalloc byte[sizeof(uint)];
			ReadExactly(buffer);

			return BinaryPrimitives.ReadUInt32BigEndian(buffer);
		}

		public T ReadUInt32<T>() where T : Enum
		{
			return (T)Enum.ToObject(typeof(T), ReadUInt32());
		}

		public uint ReadUInt32LittleEndian()
		{
			Span<byte> buffer = stackalloc byte[sizeof(uint)];
			ReadExactly(buffer);

			return BinaryPrimitives.ReadUInt32LittleEndian(buffer);
		}

		public ulong ReadUInt64()
		{
			Span<byte> buffer = stackalloc byte[sizeof(ulong)];
			ReadExactly(buffer);

			return BinaryPrimitives.ReadUInt64BigEndian(buffer);
		}

		// A base-128 varint (protobuf LEB128) - the read counterpart to WriteVarint.
		public ulong ReadVarint()
		{
			ulong result = 0;
			var shift = 0;
			while (true)
			{
				var b = ReadByte();
				result |= (ulong)(b & 0x7F) << shift;
				if ((b & 0x80) == 0)
					return result;

				shift += 7;
				if (shift > 63)
					throw new InvalidDataException("Invalid protobuf varint (too long).");
			}
		}

		public Vector3 ReadVector3()
		{
			return new Vector3(ReadFloat(), ReadFloat(), ReadFloat());
		}

		public FaolanStream WriteArray(params ReadOnlySpan<byte> value)
		{
			Write(value);
			return this;
		}

		public FaolanStream WriteArrayByteLength(FaolanStream stream)
		{
			return WriteArrayByteLength(stream.ToArray());
		}

		public FaolanStream WriteArrayByteLength(Action<FaolanStream> write)
		{
			using var stream = new FaolanStream();
			write(stream);

			return WriteArrayByteLength(stream);
		}

		public FaolanStream WriteArrayByteLength(params ReadOnlySpan<byte> value)
		{
			WriteByte(value.Length);
			return WriteArray(value);
		}

		public FaolanStream WriteArrayUInt16Length(FaolanStream stream)
		{
			return WriteArrayUInt16Length(stream.ToArray());
		}

		public FaolanStream WriteArrayUInt16Length(Action<FaolanStream> write)
		{
			using var stream = new FaolanStream();
			write(stream);

			return WriteArrayUInt16Length(stream);
		}

		public FaolanStream WriteArrayUInt16Length(params ReadOnlySpan<byte> value)
		{
			WriteUInt16(value.Length);
			return WriteArray(value);
		}

		public FaolanStream WriteArrayUInt32Length(FaolanStream stream)
		{
			return WriteArrayUInt32Length(stream.ToArray());
		}

		public FaolanStream WriteArrayUInt32Length(Action<FaolanStream> write)
		{
			using var stream = new FaolanStream();
			write(stream);

			return WriteArrayUInt32Length(stream);
		}

		public FaolanStream WriteArrayUInt32Length(params ReadOnlySpan<byte> value)
		{
			WriteUInt32(value.Length);
			return WriteArray(value);
		}

		public FaolanStream WriteByte(IConvertible value)
		{
			return WriteByte(UncheckedConvert.ToByte(value));
		}

		public new FaolanStream WriteByte(byte value)
		{
			base.WriteByte(value);
			return this;
		}

		public FaolanStream WriteDouble(double value)
		{
			Span<byte> buffer = stackalloc byte[sizeof(double)];
			BinaryPrimitives.WriteDoubleBigEndian(buffer, value);

			return WriteArray(buffer);
		}

		public FaolanStream WriteFloat(float value)
		{
			Span<byte> buffer = stackalloc byte[sizeof(float)];
			BinaryPrimitives.WriteSingleBigEndian(buffer, value);

			return WriteArray(buffer);
		}

		public FaolanStream WriteFloatLittleEndian(float value)
		{
			Span<byte> buffer = stackalloc byte[sizeof(float)];
			BinaryPrimitives.WriteSingleLittleEndian(buffer, value);

			return WriteArray(buffer);
		}

		// The little-endian twin of WriteUInt32. Not a wire form: the client's own FILE formats (the `IOz1` settings
		// archive and the `preview.bin` inside it) are little-endian and happen to be shipped over a big-endian wire.
		public FaolanStream WriteUInt32LittleEndian(uint value)
		{
			Span<byte> buffer = stackalloc byte[sizeof(uint)];
			BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);

			return WriteArray(buffer);
		}

		// The spawn transform's playfield token: a FIXED-WIDTH 4-byte LEB128 varint (not a normal variable-length
		// varint). The fixed width is deliberate - SpawnTransformPatch overwrites the token in place - so it always
		// emits 4 bytes; tokens are < 2^28, so 4 bytes always suffice.
		public FaolanStream WritePlayfieldToken(uint token)
		{
			return WriteArray((byte)((token & 0x7F) | 0x80), (byte)(((token >> 7) & 0x7F) | 0x80), (byte)(((token >> 14) & 0x7F) | 0x80), (byte)((token >> 21) & 0x7F));
		}

		public FaolanStream WriteProtobufByteLength(IExtensible proto)
		{
			var body = proto.ToProtobuf();
			return WriteArrayByteLength(body);
		}

		public FaolanStream WriteProtobufUInt32Length(IExtensible proto)
		{
			var body = proto.ToProtobuf();
			return WriteArrayUInt32Length(body);
		}

		public FaolanStream WriteQuaternion(Quaternion value)
		{
			WriteFloat(value.X);
			WriteFloat(value.Y);
			WriteFloat(value.Z);
			return WriteFloat(value.W);
		}

		// The u16-length-prefixed form is the client's DEFAULT string framing (BinaryStream::ReadString mode 0:
		// read a u16; a value >= 0x8000 escapes to a u32 long form, which nothing we emit approaches). See ReadString
		// for why Latin1 and not ASCII/UTF-8.
		public FaolanStream WriteString(string value)
		{
			return WriteArrayUInt16Length(Encoding.Latin1.GetBytes(value ?? ""));
		}

		public FaolanStream WriteStringUInt32Length(string value)
		{
			return WriteArrayUInt32Length(Encoding.Latin1.GetBytes(value ?? ""));
		}

		public FaolanStream WriteStringByteLength(string value)
		{
			return WriteArrayByteLength(Encoding.Latin1.GetBytes(value ?? ""));
		}

		public FaolanStream WriteUInt16(IConvertible value)
		{
			return WriteUInt16(UncheckedConvert.ToUInt16(value));
		}

		public FaolanStream WriteUInt16(ushort value)
		{
			Span<byte> buffer = stackalloc byte[sizeof(ushort)];
			BinaryPrimitives.WriteUInt16BigEndian(buffer, value);

			return WriteArray(buffer);
		}

		public FaolanStream WriteUInt32(IConvertible value)
		{
			return WriteUInt32(UncheckedConvert.ToUInt32(value));
		}

		public FaolanStream WriteUInt32(uint value)
		{
			Span<byte> buffer = stackalloc byte[sizeof(uint)];
			BinaryPrimitives.WriteUInt32BigEndian(buffer, value);

			return WriteArray(buffer);
		}

		public FaolanStream WriteUInt64(IConvertible value)
		{
			return WriteUInt64(UncheckedConvert.ToUInt64(value));
		}

		public FaolanStream WriteUInt64(ulong value)
		{
			Span<byte> buffer = stackalloc byte[sizeof(ulong)];
			BinaryPrimitives.WriteUInt64BigEndian(buffer, value);

			return WriteArray(buffer);
		}

		// A base-128 varint (protobuf LEB128)
		public FaolanStream WriteVarint(ulong value)
		{
			while (value >= 0x80)
			{
				WriteByte((value & 0x7F) | 0x80);
				value >>= 7;
			}

			return WriteByte(value);
		}

		public FaolanStream WriteVector3(Vector3 value)
		{
			WriteFloat(value.X);
			WriteFloat(value.Y);
			return WriteFloat(value.Z);
		}
	}
}
