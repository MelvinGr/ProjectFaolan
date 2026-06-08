using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Faolan.Importer;
using Xunit;

namespace Faolan.Tests
{
	// A MapChange opens the world-build window; the following download 0x2000 packets are collected until the first
	// 0x2020 poll ends it. The captured instance is read from the receiver conversation id in the packet header.
	public class MapWorldBurstExtractorTests
	{
		// The receiver conversation id "...47 C1 67 6C 10 <varint>" - its trailing varint is the local instance.
		private static readonly byte[] ReceiverMarker = [0x47, 0xC1, 0x67, 0x6C, 0x10];

		[Fact]
		public void Extract_CollectsBurst_UntilFirstPoll_AndCapturesInstance()
		{
			const uint instance = 0x02165EFF; // in-range 4-byte-varint instance
			var packets = new List<CapturedPacket>
			{
				MapChange(2040, instance),                     // header carries the receiver instance
				Download2000(0x66AEDD50),                      // a world object
				Download2000(0xFA015807),                      // an NPC spawn
				Poll2020(),                                    // ends the window
				Download2000(0x4517AA81)                       // after the poll -> excluded
			};

			var bursts = MapWorldBurstExtractor.Merge(MapWorldBurstExtractor.Extract(packets));

			Assert.Single(bursts);
			var b = bursts[0];
			Assert.Equal(2040u, b.MapId);
			Assert.Equal(instance, b.CapturedInstance);
			Assert.Equal(3, b.Packets.Count); // MapChange + object + NPC spawn (poll + post-poll excluded)
		}

		[Fact]
		public void Merge_DropsBurst_WhenInstanceOutOfVarintRange()
		{
			// Maps 1000/11040 captured an out-of-range instance (e.g. 0x110099BD) the length-preserving rewrite
			// can't re-address - those bursts must be dropped, not stored.
			var packets = new List<CapturedPacket>
			{
				MapChange(1000, 0x110099BD),
				Download2000(0x66AEDD50),
				Poll2020()
			};

			var bursts = MapWorldBurstExtractor.Merge(MapWorldBurstExtractor.Extract(packets));

			Assert.Empty(bursts);
		}

		private static CapturedPacket MapChange(uint mapId, uint instance)
		{
			var content = new byte[34];
			BinaryPrimitives.WriteUInt32BigEndian(content.AsSpan(0, 4), 0x5D85BFC7);
			BinaryPrimitives.WriteUInt32BigEndian(content.AsSpan(30, 4), mapId);
			return Packet(0x2000, true, content, ReceiverHeader(instance));
		}

		private static CapturedPacket Download2000(uint subop)
		{
			var content = new byte[38];
			BinaryPrimitives.WriteUInt32BigEndian(content.AsSpan(0, 4), subop);
			return Packet(0x2000, true, content);
		}

		private static CapturedPacket Poll2020() => Packet(0x2020, false, new byte[8]);

		// Receiver conversation id = marker + little-endian base-128 varint of the instance.
		private static byte[] ReceiverHeader(uint instance)
		{
			var varint = new List<byte>();
			var v = instance;
			do
			{
				var b = (byte)(v & 0x7F);
				v >>= 7;
				if (v != 0)
					b |= 0x80;
				varint.Add(b);
			} while (v != 0);

			var header = new byte[ReceiverMarker.Length + varint.Count];
			ReceiverMarker.CopyTo(header, 0);
			varint.CopyTo(header, ReceiverMarker.Length);
			return header;
		}

		private static CapturedPacket Packet(uint opcode, bool isDownload, byte[] content, byte[] header = null)
		{
			header ??= [];

			var body = new byte[4 + content.Length];
			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(0, 4), (uint)content.Length);
			content.CopyTo(body, 4);

			var full = new byte[12 + header.Length + body.Length];
			BinaryPrimitives.WriteUInt32BigEndian(full.AsSpan(8, 4), (uint)header.Length); // headerLen @ full[8..12]
			header.CopyTo(full, 12);
			body.CopyTo(full, 12 + header.Length);

			return new CapturedPacket(0, isDownload, opcode, 0, full);
		}
	}
}
