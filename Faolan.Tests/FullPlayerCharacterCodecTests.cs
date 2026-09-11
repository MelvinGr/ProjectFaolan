using System;
using System.IO;
using System.Text;
using Faolan.Core;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Protobuf;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// The reader/writer pair for the 0x737A6DF9 body must agree on the prefix model. This is the test that makes
	// that structural rather than hopeful.
	//
	// One type owns both halves of the model - FullPlayerCharacterCodec parses the prefix and writes it - and these
	// assert the round trip closes. Stating it twice lets the two drift apart: a reader that skips a 3-byte "field5"
	// a writer appends verbatim is self-consistent on the level-1 beach capture, so every golden passes while the
	// reader fails on every level-80 snapshot.
	public class FullPlayerCharacterCodecTests
	{
		private const uint Instance = 0x02165EFF;

		// The prefix the writer emits is exactly the prefix the reader consumes. Asserted for a spread of root lengths,
		// not just the captured one, because a fixed-size assumption holds for one capture and nothing else.
		[Theory, InlineData(0), InlineData(1), InlineData(686), InlineData(11003), InlineData(1 << 20)]
		// the beach capture's own root length
		// NecroL80's L80 root
		public void PrefixWritten_IsThePrefixRead(int rootLength)
		{
			var prefix = SnapshotPackets.BuildPrefix(rootLength);
			Assert.Equal(SnapshotPackets.PrefixLength, prefix.Length);

			// Feed prefix + a root of that length and check the reader delimits it exactly where the writer said.
			var body = new FaolanStream().WriteArray(prefix).WriteArray(new byte[rootLength]).WriteArray("TAILS"u8.ToArray()).ToArray();
			var stream = new FaolanStream(body);
			stream.ReadArray(1); // flag
			Assert.Equal((uint)rootLength, stream.ReadUInt32());
			stream.ReadArray((uint)rootLength);
			Assert.Equal("TAILS", Encoding.ASCII.GetString(stream.ReadRemainingArray()));
		}

		// Writer -> reader, over the real captured regions: what the writer assembles must split back into exactly
		// the parts that went in.
		[Fact]
		public void WriterOutput_SplitsBackIntoItsOwnInputs()
		{
			var (root, subClass) = SplitBeach();

			var body = SnapshotPackets.BuildBody(Instance, root, subClass);

			var stream = new FaolanStream(body);
			stream.ReadUInt32(); // record length
			Assert.Equal(FullPlayerCharacterCodec.Opcode, stream.ReadUInt32());
			Assert.Equal(EntityClass.Character, stream.ReadUInt32());
			Assert.Equal(Instance, stream.ReadUInt32());

			var parts = SnapshotPackets.Parse(stream);
			Assert.True(parts.Root.ToProtobuf().SequenceEqual(root.ToProtobuf()), "root must survive the writer/reader round trip");
			Assert.True(FullCharSnapshotSubClassCodec.Build(parts.SubClass).SequenceEqual(FullCharSnapshotSubClassCodec.Build(subClass)), "sub-class must survive the writer/reader round trip");
		}

		private static (ProtoFullCharacterIIR Root, FullCharSnapshotSubClass SubClass) SplitBeach()
		{
			var packet = Rpc.Parse(File.ReadAllBytes(TestPaths.Asset("strie-login-inventory.bin")));
			var stream = new FaolanStream(packet.BodyReader().ReadArrayUInt32Length());
			stream.ReadUInt32();
			stream.ReadUInt32();
			stream.ReadUInt32();
			var parts = SnapshotPackets.Parse(stream);
			return (parts.Root, parts.SubClass);
		}
	}
}
