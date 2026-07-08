using System.IO;
using System.IO.Compression;
using System.Linq;
using Faolan.Core;
using Faolan.Core.Network.Packets.Conan;
using Xunit;

namespace Faolan.Tests
{
	// DoS-hardening of the untrusted inbound path (security review 2026-07-02):
	//   1. FaolanStream.ReadArray rejects a length-prefix larger than the bytes remaining (no multi-GB alloc).
	//   2. ConanMessageFramer never inflates inbound: the 0x80000005 compression marker is SKIPPED as a fixed 9-byte
	//      control frame, so a compressed payload behind it is never materialised (no zlib decompression bomb).
	public class NetworkHardeningTests
	{
		[Fact]
		public void Framer_DecompressionBomb_IsDroppedNotInflatedToOom()
		{
			// ~20 MB of zeros compresses to a few KB. The framer must not inflate it: it skips the 9-byte marker, then
			// reads the zlib payload's first 4 bytes as a length prefix (>16 MB / high-bit set) and rejects it - so no
			// packet is produced and nothing is materialised (no OOM).
			var bomb = new byte[20 * 1024 * 1024];
			using var ms = new MemoryStream();
			using (var z = new ZLibStream(ms, CompressionLevel.Optimal, true))
				z.Write(bomb, 0, bomb.Length);
			var compressed = ms.ToArray();

			// The marker is a fixed 9-byte control frame [0x80 00 00 05][5 bytes]; the zlib data follows it.
			byte[] input = [0x80, 0x00, 0x00, 0x05, 0, 0, 0, 0, 0, .. compressed];

			var framer = new ConanMessageFramer();
			var packets = framer.PushBytes(input, input.Length).ToList();

			Assert.Empty(packets);
		}

		[Fact]
		public void Framer_CompressionMarkerCoalescedWithPacket_KeepsThePacket()
		{
			// The regression: a recv that TCP-coalesced the client's 9-byte compression marker with the packet behind
			// it (e.g. the post-ready 0x205f) must still frame that packet - the old path dropped the whole buffer.
			byte[] marker = [0x80, 0x00, 0x00, 0x05, 0xA5, 0x05, 0xDF, 0x1B, 0x01];
			byte[] packet = [0x00, 0x00, 0x00, 0x04, 0xAA, 0xBB, 0xCC, 0xDD]; // [len=4][4-byte body]

			var framer = new ConanMessageFramer();
			var frames = framer.PushBytes([.. marker, .. packet], marker.Length + packet.Length).ToList();

			Assert.Single(frames);
			Assert.Equal(packet, frames[0]);
		}

		[Fact]
		public void Framer_CompressionMarkerSplitAcrossPushes_IsSkipped()
		{
			// The marker can straddle two recvs; the skip state must carry across PushBytes calls and still not eat the
			// following packet.
			var framer = new ConanMessageFramer();

			Assert.Empty(framer.PushBytes([0x80, 0x00, 0x00, 0x05, 0xA5], 5).ToList()); // tag + 1 of 5 trailing bytes

			byte[] rest = [0x05, 0xDF, 0x1B, 0x01, 0x00, 0x00, 0x00, 0x04, 0xAA, 0xBB, 0xCC, 0xDD]; // 4 marker bytes + packet
			var frames = framer.PushBytes(rest, rest.Length).ToList();

			Assert.Single(frames);
			Assert.Equal([0x00, 0x00, 0x00, 0x04, 0xAA, 0xBB, 0xCC, 0xDD], frames[0]);
		}

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
	}
}
