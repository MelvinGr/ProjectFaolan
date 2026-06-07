using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Faolan.Importer;
using Xunit;

namespace Faolan.PacketAnalyzer.Tests
{
	// A MapChange opens the world-build window; the following download 0x2000 packets are collected (capturing the
	// player's own spawn handle as the instance) until the first 0x2020 poll ends it.
	public class MapWorldBurstExtractorTests
	{
		[Fact]
		public void Extract_CollectsBurst_UntilFirstPoll_AndCapturesInstance()
		{
			var packets = new List<CapturedPacket>
			{
				MapChange(2040),
				Download2000(0x66AEDD50),                      // a world object
				Spawn(handle: 0x08006F31),                     // player own spawn -> captured instance
				Download2000(0xFA015807),                      // an NPC spawn (small handle, just part of the burst)
				Poll2020(),                                    // ends the window
				Download2000(0x4517AA81)                       // after the poll -> excluded
			};

			var bursts = MapWorldBurstExtractor.Merge(MapWorldBurstExtractor.Extract(packets));

			Assert.Single(bursts);
			var b = bursts[0];
			Assert.Equal(2040u, b.MapId);
			Assert.Equal(0x08006F31u, b.CapturedInstance);
			Assert.Equal(4, b.Packets.Count); // MapChange + object + player spawn + NPC spawn (poll + post-poll excluded)
		}

		private static CapturedPacket MapChange(uint mapId)
		{
			var content = new byte[34];
			BinaryPrimitives.WriteUInt32BigEndian(content.AsSpan(0, 4), 0x5D85BFC7);
			BinaryPrimitives.WriteUInt32BigEndian(content.AsSpan(30, 4), mapId);
			return Packet(0x2000, true, content);
		}

		private static CapturedPacket Spawn(uint handle)
		{
			var content = new byte[38];
			BinaryPrimitives.WriteUInt32BigEndian(content.AsSpan(0, 4), 0xFA015807);
			BinaryPrimitives.WriteUInt32BigEndian(content.AsSpan(8, 4), handle); // body@12
			return Packet(0x2000, true, content);
		}

		private static CapturedPacket Download2000(uint subop)
		{
			var content = new byte[38];
			BinaryPrimitives.WriteUInt32BigEndian(content.AsSpan(0, 4), subop);
			return Packet(0x2000, true, content);
		}

		private static CapturedPacket Poll2020() => Packet(0x2020, false, new byte[8]);

		private static CapturedPacket Packet(uint opcode, bool isDownload, byte[] content)
		{
			var body = new byte[4 + content.Length];
			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(0, 4), (uint)content.Length);
			content.CopyTo(body, 4);

			var full = new byte[12 + body.Length]; // headerLen (full[8..12]) stays 0
			body.CopyTo(full, 12);

			return new CapturedPacket(0, isDownload, opcode, 0, full);
		}
	}
}
