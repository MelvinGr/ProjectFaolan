using System;
using System.IO;
using Faolan.Core;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Protobuf;
using Faolan.GameServer;
using Faolan.Tests.Support;
using Xunit;
using Faolan.GameServer.Wire;

namespace Faolan.Tests
{
	// Proves the from-scratch world-entry record framers reproduce their captured packets byte-for-byte when
	// given the capture's own record body - i.e. the shared RpcHeader, the IIR record framing and (for the snapshot)
	// the 5-byte blob prefix are all assembled from the live session with no captured packet as a template. The body
	// generation is proven separately (FullCharIirRoundTripTests / SnapshotTailsFromScratchTests / the SpawnPacketBuilder
	// override engine, which the live login path already exercises in-client).
	public class WorldEntryRecordFramerTests
	{
		private const uint StriePlayerInstance = 0x02165EFF;
		private static readonly string PlayerSpawnCap = TestPaths.Asset("assassin-login-playerspawn.bin");
		private static readonly string SnapshotCap = TestPaths.Asset("strie-login-inventory.bin");
		private static readonly string WorldObjectCap = TestPaths.Asset("strie-login-worldobject.bin");

		// The 5-byte blob prefix built from scratch equals the captured beach prefix (the BE32 blobLen model).
		[Fact]
		public void BuildSnapshotPrefix_MatchesCapturedBeachPrefix()
		{
			var (root, _) = SplitBeachSnapshot();
			var packet = Rpc.Parse(File.ReadAllBytes(SnapshotCap));
			var stream = new FaolanStream(packet.BodyReader().ReadArrayUInt32Length());
			stream.ReadUInt32();
			stream.ReadUInt32();
			stream.ReadUInt32();
			var parts = SnapshotPackets.Parse(stream);

			var built = SnapshotPackets.BuildPrefix(root.ToProtobuf().Length);
			Assert.Equal(SnapshotPackets.PrefixOf(parts), built);
		}

		[Fact]
		public void BuildWorldEntrySnapshot_ReproducesCapturedBeachPacket()
		{
			var captured = File.ReadAllBytes(SnapshotCap);
			var (root, tails) = SplitBeachSnapshot();

			var built = GameWire.BuildWorldEntrySnapshot(BeachClient(), root, tails).Serialize();

			Assert.Equal(captured.Length, built.Length);
			Assert.True(captured.SequenceEqual(built), "from-scratch snapshot packet must be byte-identical to the capture");
		}

		// The player FA015807 spawn packet, framed from scratch around its captured record body, reproduces the
		// captured packet (header + outer length/CRC). The body is the `packet[bodyStart..]` form the override engine
		// produces; here we feed the captured body to isolate the framing.
		[Fact]
		public void BuildWorldEntrySpawn_ReproducesCapturedPlayerSpawnPacket()
		{
			var captured = File.ReadAllBytes(PlayerSpawnCap);
			var bodyStart = 12 + (int)captured.ReadU32Be(8);
			// The record, without the carrier's u32 length - that belongs to the sender, which is what this frames.
			var spawnRecord = captured[(bodyStart + 4)..];

			var built = GameWire.BuildWorldEntrySpawn(BeachClient(), spawnRecord).Serialize();

			Assert.Equal(captured.Length, built.Length);
			Assert.True(captured.SequenceEqual(built), "from-scratch player-spawn packet must be byte-identical to the capture");
		}

		// A world-object record (66AEDD50, handle != the player) frames with the same shared header as the snapshot /
		// player spawn - confirming the keystone covers every world-entry record class, not just the player's own.
		[Fact]
		public void FrameWorldEntryRecord_ReproducesCapturedWorldObjectPacket()
		{
			var captured = File.ReadAllBytes(WorldObjectCap);
			var bodyStart = 12 + (int)captured.ReadU32Be(8);
			var objectBody = captured[bodyStart..];

			var built = GameWire.FrameWorldEntryRecord(BeachClient(), objectBody).Serialize();

			Assert.Equal(captured.Length, built.Length);
			Assert.True(captured.SequenceEqual(built), "from-scratch world-object packet must be byte-identical to the capture");
		}

		private static RecordingNetworkClient BeachClient()
		{
			return new RecordingNetworkClient(StriePlayerInstance);
		}

		private static (ProtoFullCharacterIIR Root, FullCharSnapshotSubClass SubClass) SplitBeachSnapshot()
		{
			var packet = Rpc.Parse(File.ReadAllBytes(SnapshotCap));
			var stream = new FaolanStream(packet.BodyReader().ReadArrayUInt32Length());
			stream.ReadUInt32();
			stream.ReadUInt32();
			stream.ReadUInt32();
			var parts = SnapshotPackets.Parse(stream);
			return (parts.Root, parts.SubClass);
		}
	}
}
