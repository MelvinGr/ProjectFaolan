using System.IO;
using System.Linq;
using Faolan.Core.Data;
using Faolan.Core.Network.Conan;
using Faolan.GameServer;
using Faolan.GameServer.Packets.FullCharSnapshot;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// Proves the from-scratch world-entry snapshot framer (#210) reproduces the captured beach 0x737A6DF9 packet
	// byte-for-byte when given that capture's own root + tails - i.e. the RpcHeader, the IIR record framing and the
	// 8-byte blob prefix are all assembled from the live session with NO captured packet as a template. (The root and
	// tails generation are proven separately: FullCharIirRoundTripTests / BeachTailsFromScratchTests.)
	public class WorldEntrySnapshotFramerTests
	{
		private const string Cap = "/home/melvin/Development/C#/Project Faolan/Captures/strie-login-inventory.bin";
		private const uint StriePlayerInstance = 0x02165EFF;

		private static (byte[] Root, byte[] Tails) SplitBeach()
		{
			var packet = new ConanPacket(File.ReadAllBytes(Cap));
			var stream = new FaolanStream(packet.Body.ReadArrayUInt32Length());
			stream.ReadUInt32(); stream.ReadUInt32(); stream.ReadUInt32();
			var parts = InventorySnapshotInspector.ParseInventoryPacketBody(stream);
			return (parts.Root.ToArray(), parts.Tails.ToArray());
		}

		[Fact]
		public void BuildWorldEntrySnapshot_ReproducesCapturedBeachPacket()
		{
			var captured = File.ReadAllBytes(Cap);
			var (root, tails) = SplitBeach();

			var built = GameServerListener.BuildWorldEntrySnapshot(BeachClient(), root, tails);

			Assert.Equal(captured.Length, built.Length);
			Assert.True(captured.SequenceEqual(built), "from-scratch snapshot packet must be byte-identical to the capture");
		}

		// The 8-byte blob prefix built from scratch equals the captured beach prefix (the BE32 blobLen model).
		[Fact]
		public void BuildSnapshotPrefix_MatchesCapturedBeachPrefix()
		{
			var packet = new ConanPacket(File.ReadAllBytes(Cap));
			var stream = new FaolanStream(packet.Body.ReadArrayUInt32Length());
			stream.ReadUInt32(); stream.ReadUInt32(); stream.ReadUInt32();
			var parts = InventorySnapshotInspector.ParseInventoryPacketBody(stream);

			var built = GameServerListener.BuildSnapshotPrefix(parts.Root.ToArray().Length);
			Assert.Equal(parts.Prefix, built);
		}

		private static RecordingNetworkClient BeachClient() => new(StriePlayerInstance);
	}
}
