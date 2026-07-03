using System.Collections.Generic;
using System.IO;

namespace Faolan.Core.Network.Conan
{
	// The single canonical base-128 varint (protobuf LEB128) codec for the Conan wire format. Replaces the
	// hand-rolled EncodeVarint / WriteVarint copies that used to live in PacketBurstHelper, SnapshotPrefix,
	// ConversationReplay, ScryScript, PlayerSpawnScaffoldCodec and ProtoMessage. Use FaolanStream.WriteVarint for
	// stream writes; these statics are for the byte[]/List<byte> builders.
	public static class Varint
	{
		// The base-128 varint encoding of `value`.
		public static byte[] Encode(ulong value)
		{
			var bytes = new List<byte>(5);
			Write(bytes, value);
			return bytes.ToArray();
		}

		// The number of bytes Encode(value) produces (without allocating).
		public static int Length(ulong value)
		{
			var length = 1;
			while (value >= 0x80)
			{
				value >>= 7;
				length++;
			}

			return length;
		}

		// Reads a base-128 varint from `stream`.
		public static ulong Read(Stream stream)
		{
			ulong result = 0;
			var shift = 0;
			while (true)
			{
				var value = stream.ReadByte();
				if (value < 0)
					throw new InvalidDataException("Unexpected end of protobuf varint.");

				var b = (byte)value;
				result |= (ulong)(b & 0x7F) << shift;
				if ((b & 0x80) == 0)
					return result;

				shift += 7;
				if (shift > 63)
					throw new InvalidDataException("Invalid protobuf varint (too long).");
			}
		}

		// Appends `value` as a base-128 varint to `bytes`.
		public static void Write(ICollection<byte> bytes, ulong value)
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
