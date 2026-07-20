using System;
using System.IO;
using System.Linq;
using Faolan.Core;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Packets.Conan;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// The reader/writer pair for the 0x737A6DF9 body must agree on the prefix model. This is the test that makes
	// that structural rather than hopeful.
	//
	// The model used to be stated TWICE - FullCharSnapshotCodec parsed the prefix, and the server host emitted it
	// from a separate builder in another project. They disagreed: the reader skipped a 3-byte "field5" that the
	// writer appended verbatim. Both were self-consistent on the level-1 beach capture, so every existing golden
	// passed while the reader failed on EVERY level-80 snapshot. Now one type owns the writing
	// (FullCharSnapshotCodec) and these assert the round trip closes.
	public class FullCharSnapshotCodecTests
	{
		private const uint Instance = 0x02165EFF;

		// Writer -> reader, over the real captured regions: what the writer assembles must split back into exactly
		// the parts that went in.
		[Fact]
		public void WriterOutput_SplitsBackIntoItsOwnInputs()
		{
			var (root, subClass) = SplitBeach();

			var body = FullCharSnapshotCodec.BuildBody(Instance, root, subClass);

			var stream = new FaolanStream(body);
			stream.ReadUInt32(); // record length
			Assert.Equal(FullPlayerCharacterBuilder.Opcode, stream.ReadUInt32());
			Assert.Equal(FullCharSnapshotCodec.TypeTag, stream.ReadUInt32());
			Assert.Equal(Instance, stream.ReadUInt32());

			var parts = FullCharSnapshotCodec.ParseInventoryPacketBody(stream);
			Assert.True(parts.Root.ToProtobuf().SequenceEqual(root), "root must survive the writer/reader round trip");
			Assert.True(parts.SubClass.ToArray().SequenceEqual(subClass), "sub-class must survive the writer/reader round trip");
		}

		// The prefix the writer emits is exactly the prefix the reader consumes - the specific invariant that was
		// broken. Asserted for a spread of root lengths, not just the captured one, because the old bug was a
		// FIXED-SIZE assumption that only held for one capture.
		[Theory]
		[InlineData(0)]
		[InlineData(1)]
		[InlineData(686)] // the beach capture's own root length
		[InlineData(11003)] // NecroL80's L80 root
		[InlineData(1 << 20)]
		public void PrefixWritten_IsThePrefixRead(int rootLength)
		{
			var prefix = FullCharSnapshotCodec.BuildPrefix(rootLength);
			Assert.Equal(FullCharSnapshotCodec.PrefixLength, prefix.Length);

			// Feed prefix + a root of that length and check the reader delimits it exactly where the writer said.
			var body = new FaolanStream().WriteArray(prefix).WriteArray(new byte[rootLength]).WriteArray("TAILS"u8.ToArray()).ToArray();
			var stream = new FaolanStream(body);
			stream.ReadArray(1); // flag
			Assert.Equal((uint)rootLength, stream.ReadUInt32());
			stream.ReadArray((uint)rootLength);
			Assert.Equal("TAILS", System.Text.Encoding.ASCII.GetString(stream.ReadRemainingArray()));
		}

		private static (byte[] Root, byte[] SubClass) SplitBeach()
		{
			var packet = new ConanPacket(File.ReadAllBytes(TestPaths.Asset("strie-login-inventory.bin")));
			var stream = new FaolanStream(packet.Body.ReadArrayUInt32Length());
			stream.ReadUInt32();
			stream.ReadUInt32();
			stream.ReadUInt32();
			var parts = FullCharSnapshotCodec.ParseInventoryPacketBody(stream);
			return (parts.Root.ToProtobuf(), parts.SubClass.ToArray());
		}
	}
}
