using System;
using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Text;
using Faolan.Core.Extensions;

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
			return checked((byte)base.ReadByte());
		}

		public float ReadFloat()
		{
			Span<byte> buffer = stackalloc byte[sizeof(float)];
			ReadExactly(buffer);

			return BinaryPrimitives.ReadSingleBigEndian(buffer);
		}

		public T ReadProtobufByteLength<T>()
		{
			var body = ReadArrayByteLength();
			return body.FromProtobuf<T>();
		}

		public T ReadProtobufUInt32Length<T>()
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

		public string ReadString()
		{
			return Encoding.UTF8.GetString(ReadArrayUInt16Length());
		}

		public string ReadStringByteLength()
		{
			return Encoding.UTF8.GetString(ReadArrayByteLength());
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

		public ulong ReadUInt64()
		{
			Span<byte> buffer = stackalloc byte[sizeof(ulong)];
			ReadExactly(buffer);

			return BinaryPrimitives.ReadUInt64BigEndian(buffer);
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

		// Big-endian double for RAW packet bodies (not protobuf fields). Matches the wire format for the
		// OverrideGameTweaks values; see WriteFloatBigEndian above for the raw-body endianness rationale.
		public FaolanStream WriteDoubleBigEndian(double value)
		{
			Span<byte> buffer = stackalloc byte[sizeof(double)];
			BinaryPrimitives.WriteDoubleBigEndian(buffer, value);

			return WriteArray(buffer);
		}

		// RESOLVED (2026-06-08, from retail captures): the wire uses TWO float encodings depending on context, which
		// is why WriteFloat (LE) and ReadFloat (BE) intentionally differ - they serve different contexts:
		//   * Protobuf fixed32 fields (tags 0x0D/0x15/0x1D/0x25, e.g. Send0x2000_SpawnPlayer, texture floats) are
		//     little-endian by the protobuf spec. WriteFloat is LE because that is its dominant use; flipping it to
		//     big-endian corrupts those fields and crashes the client (the old "client crashes with Big endian").
		//   * Raw packet bodies (MapChange position @ body+13, Move 0x4517AA81 position/rotation) are big-endian.
		//     ReadFloat is BE because it parses those raw client uploads. Verified: client Move sends 995.3/115.2/...
		//     as BE; the protobuf spawn sends 1019.36/127.74/... as LE.
		// So: use WriteFloat/WriteVector3 in protobuf contexts, WriteFloatBigEndian/WriteVector3BigEndian in raw ones.
		public FaolanStream WriteFloat(float value)
		{
			Span<byte> buffer = stackalloc byte[sizeof(float)];
			BinaryPrimitives.WriteSingleLittleEndian(buffer, value); // protobuf fixed32 is little-endian; see note above

			return WriteArray(buffer);
		}

		// Big-endian float for RAW packet bodies (not protobuf fields). Matches ReadFloat and the retail wire format
		// for raw positions (MapChange, Move). See the WriteFloat note above.
		public FaolanStream WriteFloatBigEndian(float value)
		{
			Span<byte> buffer = stackalloc byte[sizeof(float)];
			BinaryPrimitives.WriteSingleBigEndian(buffer, value);

			return WriteArray(buffer);
		}

		public FaolanStream WriteQuaternion(Quaternion value)
		{
			WriteFloat(value.X);
			WriteFloat(value.Y);
			WriteFloat(value.Z);
			return WriteFloat(value.W);
		}

		public FaolanStream WriteQuaternionBigEndian(Quaternion value)
		{
			WriteFloatBigEndian(value.X);
			WriteFloatBigEndian(value.Y);
			WriteFloatBigEndian(value.Z);
			return WriteFloatBigEndian(value.W);
		}

		public FaolanStream WriteString(string value)
		{
			return WriteArrayUInt16Length(Encoding.UTF8.GetBytes(value ?? ""));
		}

		public FaolanStream WriteStringByteLength(string value)
		{
			return WriteArrayByteLength(Encoding.UTF8.GetBytes(value ?? ""));
		}

		public FaolanStream WriteByte(IConvertible value)
		{
			return WriteByte(Convert.ToByte(value));
		}

		public new FaolanStream WriteByte(byte value)
		{
			base.WriteByte(value);
			return this;
		}

		public FaolanStream WriteUInt16(IConvertible value)
		{
			return WriteUInt16(Convert.ToUInt16(value));
		}

		public FaolanStream WriteUInt16(ushort value)
		{
			Span<byte> buffer = stackalloc byte[sizeof(ushort)];
			BinaryPrimitives.WriteUInt16BigEndian(buffer, value);

			return WriteArray(buffer);
		}

		public FaolanStream WriteUInt32(IConvertible value)
		{
			return WriteUInt32(Convert.ToUInt32(value));
		}

		public FaolanStream WriteUInt32(uint value)
		{
			Span<byte> buffer = stackalloc byte[sizeof(uint)];
			BinaryPrimitives.WriteUInt32BigEndian(buffer, value);

			return WriteArray(buffer);
		}

		public FaolanStream WriteUInt64(IConvertible value)
		{
			return WriteUInt64(Convert.ToUInt64(value));
		}

		public FaolanStream WriteUInt64(ulong value)
		{
			Span<byte> buffer = stackalloc byte[sizeof(ulong)];
			BinaryPrimitives.WriteUInt64BigEndian(buffer, value);

			return WriteArray(buffer);
		}

		// A base-128 varint (protobuf LEB128) - the canonical Conan wire varint (Network.Conan.Varint).
		public FaolanStream WriteVarint(ulong value)
		{
			while (value >= 0x80)
			{
				WriteByte((byte)((value & 0x7F) | 0x80));
				value >>= 7;
			}

			return WriteByte((byte)value);
		}

		public FaolanStream WriteVector3(Vector3 value)
		{
			WriteFloat(value.X);
			WriteFloat(value.Y);
			return WriteFloat(value.Z);
		}

		// Big-endian Vector3 for raw packet bodies (the round-trip partner of ReadVector3).
		public FaolanStream WriteVector3BigEndian(Vector3 value)
		{
			WriteFloatBigEndian(value.X);
			WriteFloatBigEndian(value.Y);
			return WriteFloatBigEndian(value.Z);
		}
	}
}
