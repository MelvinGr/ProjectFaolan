using System.Collections.Generic;

namespace Faolan.GameServer.Packets.FullCharSnapshot
{
	// Constructs the beach 0x737A6DF9 snapshot's 8-byte IIR prefix FROM SCRATCH, instead of carrying the captured
	// prefix and delta-adjusting its length varint (AdjustSnapshotPrefixRootLength). Layout, recovered by diffing
	// captures:
	//   [00 00 00 02]              base-class IIR version (2)
	//   [varint = const + rootLen] the root-length field the client uses to delimit the protobuf root from the
	//                              trailing subclass (tails); the constant is a fixed offset into the (server-owned)
	//                              beach tails template, so it is constant for the beach snapshot.
	//   [81 06]                    trailing varint (769), constant for the beach template.
	// Proven across two beach captures (varint - rootLen = 4483 in both) and byte-reconstructs the captured prefix.
	public static class BeachSnapshotPrefix
	{
		// varint value = RootLengthConstant + rootLength. Pinned from the beach captures (strie root 683 / varint
		// 5166; bigboy root 678 / varint 5161).
		public const ulong RootLengthConstant = 4483;

		private static readonly byte[] BaseVersion = [0x00, 0x00, 0x00, 0x02];
		private static readonly byte[] Trailing = [0x81, 0x06];

		public static byte[] Build(int rootLength)
		{
			var prefix = new List<byte>(8);
			prefix.AddRange(BaseVersion);
			prefix.AddRange(EncodeVarint(RootLengthConstant + (ulong)rootLength));
			prefix.AddRange(Trailing);
			return [.. prefix];
		}

		private static IEnumerable<byte> EncodeVarint(ulong v)
		{
			var bytes = new List<byte>();
			do
			{
				var b = (byte)(v & 0x7F);
				v >>= 7;
				if (v != 0)
					b |= 0x80;
				bytes.Add(b);
			} while (v != 0);

			return bytes;
		}
	}
}
