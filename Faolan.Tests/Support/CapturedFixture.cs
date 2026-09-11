using System.Collections.Generic;
using System.IO;
using Faolan.Core.Extensions;

namespace Faolan.Tests.Support
{
	// Multi-packet capture fixtures under Faolan.Tests/Assets: whole Conan packets concatenated back to back.
	//
	// No container format is needed because a packet already frames itself - it opens with a u32 counting every byte
	// after that word ([u32 length][u32 crc32][u32 headerLen][header][body], Rpc.cs), so the stream is walked by
	// following the prefix, and the IIR type is read out of the body exactly as CorpusExtract does. Callers never
	// have to name the type a fixture holds, which is what keeps a mislabelled fixture from quietly changing what an
	// extractor dispatches on.
	//
	// These exist so the tests that only ever wanted one packet (or one capture's worth) stop reading the 14 MB
	// whole-corpus extract: the recipe book, the mount spawns, the Casilda quest packets. A gate that reads the
	// corpus for its breadth keeps reading the corpus - see CorpusExtract.
	public static class CapturedFixture
	{
		// Every packet in `assetName`, in capture order.
		public static IReadOnlyList<Entry> Packets(string assetName)
		{
			var raw = TestPaths.AssetBytes(assetName);
			var packets = new List<Entry>();

			var offset = 0;
			while (offset < raw.Length)
			{
				// The prefix counts everything after itself, so the packet spans [offset, offset + 4 + length).
				var length = (int)raw.ReadU32Be(offset);
				var end = offset + 4 + length;
				if (length <= 0 || end > raw.Length)
					throw new InvalidDataException($"{assetName} is truncated: packet at offset {offset} declares {length} bytes, only {raw.Length - offset - 4} remain.");

				var full = raw[offset..end];
				var headerLength = (int)full.ReadU32Be(8);
				packets.Add(new Entry(full.ReadU32Be(12 + headerLength + 4), full));
				offset = end;
			}

			return packets;
		}

		// One captured packet: the full wire bytes plus the IIR type read out of its body.
		public sealed record Entry(uint Iir, byte[] Full);
	}
}
