using System;
using System.Collections.Generic;

namespace Faolan.Core.Network.Packets
{
    // The canonical base-128 varint (protobuf LEB128) codec for the Conan wire, for the byte[]/List<byte> builders
    // (ScryScript, CombatService). FaolanStream.Read/WriteVarint is the stream-side equivalent.
    [Obsolete("Use FaolanStream")]
    public static class Varint
    {
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
