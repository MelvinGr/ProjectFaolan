using System;
using System.Collections.Generic;
using System.IO;

namespace Faolan.Core.Network.Packets
{
	// The canonical base-128 varint (protobuf LEB128) codec for the Conan wire, for the byte[]/List<byte> builders
	// (ScryScript, CombatService). FaolanStream.Read/WriteVarint is the stream-side equivalent.
	[Obsolete("Use FaolanStream")]
	public static class Varint
	{
		public static ulong ReadVarint(Stream stream)
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
		public static void WriteVarint(ICollection<byte> bytes, ulong value)
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
