using System.IO;
using System.IO.Compression;
using System.Linq;
using Faolan.Core;
using Faolan.Core.Network.Conan;
using Xunit;

namespace Faolan.Tests
{
	// DoS-hardening of the untrusted inbound path (security review 2026-07-02):
	//   1. FaolanStream.ReadArray rejects a length-prefix larger than the bytes remaining (no multi-GB alloc).
	//   2. ConanMessageFramer caps inflated output (no zlib decompression bomb).
	public class NetworkHardeningTests
	{
		[Fact]
		public void ReadArrayUInt32Length_HostileLengthPrefix_ThrowsInsteadOfAllocating()
		{
			// A 0xFFFFFFFF length-prefix with no payload must be rejected, not turned into a ~4 GB allocation.
			var s = new FaolanStream([0xFF, 0xFF, 0xFF, 0xFF]);
			Assert.Throws<EndOfStreamException>(() => s.ReadArrayUInt32Length());
		}

		[Fact]
		public void ReadArrayUInt32Length_LengthBeyondRemaining_Throws()
		{
			// len=10 but only 3 payload bytes present.
			var s = new FaolanStream([0, 0, 0, 10, 0xAA, 0xBB, 0xCC]);
			Assert.Throws<EndOfStreamException>(() => s.ReadArrayUInt32Length());
		}

		[Fact]
		public void ReadArrayUInt32Length_ValidLength_StillReads()
		{
			// len=3, exactly 3 payload bytes → the guard must not break the happy path.
			var s = new FaolanStream([0, 0, 0, 3, 0xAA, 0xBB, 0xCC]);
			Assert.Equal([0xAA, 0xBB, 0xCC], s.ReadArrayUInt32Length());
		}

		[Fact]
		public void Framer_DecompressionBomb_IsDroppedNotInflatedToOom()
		{
			// ~20 MB of zeros compresses to a few KB but would inflate past the 16 MB framing cap; the framer must
			// drop it (no packet, no OOM) rather than materialise the full payload.
			var bomb = new byte[20 * 1024 * 1024];
			using var ms = new MemoryStream();
			using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
				z.Write(bomb, 0, bomb.Length);
			var compressed = ms.ToArray();

			// Framer inflate path expects [0x80 00 00 05][5 skipped bytes][zlib data].
			byte[] input = [0x80, 0x00, 0x00, 0x05, 0, 0, 0, 0, 0, .. compressed];

			var framer = new ConanMessageFramer();
			var packets = framer.PushBytes(input, input.Length).ToList();

			Assert.Empty(packets);
		}
	}
}
