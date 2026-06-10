using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Faolan.Core.Network.Packets
{
	public sealed class ProtoMessage
	{
		public List<ProtoField> Fields { get; } = [];

		public byte[] ToArray()
		{
			return Fields
				.SelectMany(x => x.ToArray())
				.ToArray();
		}

		public static ProtoMessage Parse(MemoryStream stream, int? max = null)
		{
			var message = new ProtoMessage();

			while (stream.Position < stream.Length && (max == null || message.Fields.Count < max))
				message.Fields.Add(ProtoField.Read(stream));

			return message;
		}

		public static ProtoMessage Parse(MemoryStream stream, long byteLength, int? max = null)
		{
			if (byteLength < 0)
				throw new ArgumentOutOfRangeException(nameof(byteLength));

			var end = stream.Position + byteLength;

			if (end > stream.Length)
				throw new InvalidOperationException($"Requested protobuf parse length {byteLength} exceeds stream bounds. Position={stream.Position}, Length={stream.Length}.");

			var message = new ProtoMessage();

			while (stream.Position < end && (max == null || message.Fields.Count < max))
				message.Fields.Add(ProtoField.Read(stream));

			if (stream.Position != end && max == null)
				throw new InvalidOperationException($"Protobuf parse ended at offset {stream.Position}, expected {end}.");

			return message;
		}

		public static ProtoMessage Parse(ReadOnlySpan<byte> data, int? max = null)
		{
			using var stream = new MemoryStream(data.ToArray(), false);
			return Parse(stream, max);
		}
	}

	public sealed class ProtoField
	{
		public byte[]? RawBytes { get; set; }
		public required int FieldNumber { get; init; }
		public required int WireType { get; init; }

		public ProtoMessage? MessageValue { get; set; }

		public uint Fixed32Value
		{
			get
			{
				if (WireType != 5)
					throw new InvalidOperationException($"Field {FieldNumber} is not fixed32.");

				if (RawBytes is not { Length: 4 })
					throw new InvalidOperationException($"Field {FieldNumber} does not contain 4 fixed32 bytes.");

				return BinaryPrimitives.ReadUInt32LittleEndian(RawBytes);
			}

			set
			{
				if (WireType != 5)
					throw new InvalidOperationException($"Field {FieldNumber} is not fixed32.");

				RawBytes = new byte[4];
				BinaryPrimitives.WriteUInt32LittleEndian(RawBytes, value);
			}
		}

		public ulong Fixed64Value
		{
			get
			{
				if (WireType != 1)
					throw new InvalidOperationException($"Field {FieldNumber} is not fixed64.");

				if (RawBytes is not { Length: 8 })
					throw new InvalidOperationException($"Field {FieldNumber} does not contain 8 fixed64 bytes.");

				return BinaryPrimitives.ReadUInt64LittleEndian(RawBytes);
			}

			set
			{
				if (WireType != 1)
					throw new InvalidOperationException($"Field {FieldNumber} is not fixed64.");

				RawBytes = new byte[8];
				BinaryPrimitives.WriteUInt64LittleEndian(RawBytes, value);
			}
		}

		public ulong VarintValue { get; set; }

		public byte[] ToArray()
		{
			var bytes = new List<byte>();

			WriteVarint(bytes, (ulong)((FieldNumber << 3) | WireType));

			switch (WireType)
			{
				case 0:
				{
					WriteVarint(bytes, VarintValue);
					break;
				}

				case 1:
				{
					if (RawBytes is not { Length: 8 })
						throw new InvalidOperationException($"Field {FieldNumber} is fixed64 but does not contain 8 bytes.");

					bytes.AddRange(RawBytes);
					break;
				}

				case 2:
				{
					var payload = MessageValue != null
						? MessageValue.ToArray()
						: RawBytes ?? [];

					WriteVarint(bytes, (ulong)payload.Length);
					bytes.AddRange(payload);

					break;
				}

				case 5:
				{
					if (RawBytes is not { Length: 4 })
						throw new InvalidOperationException($"Field {FieldNumber} is fixed32 but does not contain 4 bytes.");

					bytes.AddRange(RawBytes);
					break;
				}

				default:
					throw new NotSupportedException($"Unsupported protobuf wire type {WireType}.");
			}

			return bytes.ToArray();
		}

		public static ProtoField Read(MemoryStream stream)
		{
			var tag = ReadVarint(stream);

			var fieldNumber = (int)(tag >> 3);
			var wireType = (int)(tag & 7);

			if (fieldNumber == 0)
				throw new InvalidOperationException("Invalid protobuf field number 0.");

			return wireType switch
			{
				0 => new ProtoField
				{
					FieldNumber = fieldNumber,
					WireType = wireType,
					VarintValue = ReadVarint(stream)
				},

				1 => new ProtoField
				{
					FieldNumber = fieldNumber,
					WireType = wireType,
					RawBytes = ReadBytes(stream, 8)
				},

				2 => ReadLengthDelimited(stream, fieldNumber),

				5 => new ProtoField
				{
					FieldNumber = fieldNumber,
					WireType = wireType,
					RawBytes = ReadBytes(stream, 4)
				},

				_ => throw new NotSupportedException($"Unsupported protobuf wire type {wireType} at offset {stream.Position}.")
			};
		}

		private static byte[] ReadBytes(MemoryStream stream, int length)
		{
			if (length < 0)
				throw new InvalidOperationException("Cannot read a negative number of bytes.");

			if (stream.Position + length > stream.Length)
				throw new InvalidOperationException($"Attempted to read {length} bytes beyond protobuf payload bounds.");

			var bytes = new byte[length];
			var read = stream.Read(bytes, 0, length);

			if (read != length)
				throw new InvalidOperationException("Unexpected end of protobuf stream.");

			return bytes;
		}

		private static ProtoField ReadLengthDelimited(MemoryStream stream, int fieldNumber)
		{
			var length = checked((int)ReadVarint(stream));
			var bytes = ReadBytes(stream, length);

			ProtoMessage? nested = null;

			try
			{
				using var nestedStream = new MemoryStream(bytes, false);
				nested = ProtoMessage.Parse(nestedStream);
			}
			catch
			{
				// Keep as raw bytes if nested parsing fails.
			}

			return new ProtoField
			{
				FieldNumber = fieldNumber,
				WireType = 2,
				RawBytes = bytes,
				MessageValue = nested
			};
		}

		private static ulong ReadVarint(MemoryStream stream)
		{
			ulong result = 0;
			var shift = 0;

			while (true)
			{
				var value = stream.ReadByte();

				if (value < 0)
					throw new InvalidOperationException("Unexpected end of protobuf varint.");

				var b = (byte)value;

				result |= (ulong)(b & 0x7F) << shift;

				if ((b & 0x80) == 0)
					return result;

				shift += 7;

				if (shift > 63)
					throw new InvalidOperationException("Invalid protobuf varint.");
			}
		}

		private static void WriteVarint(List<byte> bytes, ulong value)
		{
			while (value >= 0x80)
			{
				bytes.Add((byte)((value & 0x7F) | 0x80));
				value >>= 7;
			}

			bytes.Add((byte)value);
		}
	}
}
