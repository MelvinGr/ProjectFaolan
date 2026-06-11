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

		private const uint Opcode2076 = 0x2076; // spawn manifest
		private static readonly byte[] ManifestSentinel = [0xDE, 0xAD, 0xBE, 0xEF]; // marks the synthetic manifest body

		[Fact]
		public void Extract_CollectsPrePollBurst_AndCapturesInstance()
		{
			const uint instance = 0x02165EFF; // in-range 4-byte-varint instance
			var packets = new List<CapturedPacket>
			{
				MapChange(2040, instance), // header carries the receiver instance
				Download2000(0x66AEDD50), // a world object
				Download2000(0xFA015807), // an NPC spawn
				Poll2020(), // ends the pre-poll burst (but NOT the window)
				Download2000(0x4517AA81) // a post-poll gameplay 0x2000 -> excluded (only 0x2076 is taken post-poll)
			};

			var bursts = MapWorldBurstExtractor.Merge(MapWorldBurstExtractor.Extract(packets));

			Assert.Single(bursts);
			var b = bursts[0];
			Assert.Equal(2040u, b.MapId);
			Assert.Equal(instance, b.CapturedInstance);
			Assert.Equal(3, b.Packets.Count); // MapChange + object + NPC spawn (poll + post-poll 0x2000 excluded)
		}

		[Fact]
		public void Extract_IncludesPostPollSpawnManifest_ButNotOtherPostPollDownloads()
		{
			const uint instance = 0x02165EFF;
			var packets = new List<CapturedPacket>
			{
				MapChange(2040, instance),
				Download2000(0xFA015807), // pre-poll entity spawn
				Poll2020(), // pre-poll burst done; the poll flood begins
				Poll2020(), // more polls
				Download2000(0xF98E10B3), // post-poll StatIir (server-generated) -> excluded
				Packet(0x2008, true, new byte[8]), // post-poll SpawnCheck (server-generated) -> excluded
				Manifest2076(), // THE spawn manifest -> included (the full-window record the beach seed carries)
				Packet(0x2024, true, new byte[8]) // ReadyForPlayScreen (server-generated) -> excluded
			};

			var bursts = MapWorldBurstExtractor.Merge(MapWorldBurstExtractor.Extract(packets));

			Assert.Single(bursts);
			var b = bursts[0];
			Assert.Equal(2040u, b.MapId);
			Assert.Equal(3, b.Packets.Count); // MapChange + entity spawn + the 0x2076 manifest
			// The one post-poll record kept is the manifest (its sentinel body), proving the StatIir / SpawnCheck /
			// ReadyForPlayScreen downloads around it were excluded.
			Assert.Contains(b.Packets, pkt => ContainsSentinel(pkt, ManifestSentinel));
		}

		[Fact]
		public void Extract_StartsFreshBurst_OnNextMapChange_AfterManifest()
		{
			const uint instance = 0x02165EFF;
			var packets = new List<CapturedPacket>
			{
				MapChange(2040, instance),
				Download2000(0xFA015807),
				Poll2020(),
				Manifest2076(),
				// a second map change in the same capture (e.g. a /changemap) starts a clean window
				MapChange(3000, instance),
				Download2000(0x66AEDD50),
				Poll2020()
			};

			var bursts = MapWorldBurstExtractor.Extract(packets);

			Assert.Equal(2, bursts.Count);
			Assert.Equal(2040u, bursts[0].MapId);
			Assert.Equal(3, bursts[0].Packets.Count); // MapChange + spawn + manifest
			Assert.Equal(3000u, bursts[1].MapId);
			Assert.Equal(2, bursts[1].Packets.Count); // MapChange + world object (manifest-less window)
		}

		[Fact]
		public void Merge_DropsBurst_WhenInstanceUnknown()
		{
			// No receiver header -> CapturedInstance stays 0 -> can't re-address -> dropped.
			var packets = new List<CapturedPacket>
			{
				Packet(0x2000, true, MapChangeContent(1000)),
				Download2000(0x66AEDD50),
				Poll2020()
			};

			var bursts = MapWorldBurstExtractor.Merge(MapWorldBurstExtractor.Extract(packets));

			Assert.Empty(bursts);
		}

		[Fact]
		public void Merge_KeepsBurst_WhenInstanceOutOfVarintRange()
		{
			// Maps 1000/11040 captured an out-of-range instance (e.g. 0x110099BD, a 5-byte varint). These are kept
			// now: RewriteBurstInstance pads the live id's varint to the captured length, so the re-address stays
			// length-preserving. Only an unknown instance (0) is dropped.
			var packets = new List<CapturedPacket>
			{
				MapChange(1000, 0x110099BD),
				Download2000(0x66AEDD50),
				Poll2020()
			};

			var bursts = MapWorldBurstExtractor.Merge(MapWorldBurstExtractor.Extract(packets));

			Assert.Single(bursts);
			Assert.Equal(1000u, bursts[0].MapId);
			Assert.Equal(0x110099BDu, bursts[0].CapturedInstance);
		}

		private static CapturedPacket Download2000(uint subop)
		{
			var content = new byte[38];
			BinaryPrimitives.WriteUInt32BigEndian(content.AsSpan(0, 4), subop);
			return Packet(0x2000, true, content);
		}

		private static CapturedPacket MapChange(uint mapId, uint instance)
		{
			return Packet(0x2000, true, MapChangeContent(mapId), ReceiverHeader(instance));
		}

		private static byte[] MapChangeContent(uint mapId)
		{
			var content = new byte[34];
			BinaryPrimitives.WriteUInt32BigEndian(content.AsSpan(0, 4), 0x5D85BFC7);
			BinaryPrimitives.WriteUInt32BigEndian(content.AsSpan(30, 4), mapId);
			return content;
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

		private static CapturedPacket Poll2020()
		{
			return Packet(0x2020, false, new byte[8]);
		}

		// The post-poll spawn manifest (opcode 0x2076), carrying a recognizable sentinel body so the test can confirm
		// THIS record - and not a server-generated neighbour - is the one kept from the post-poll region.
		private static CapturedPacket Manifest2076()
		{
			return Packet(Opcode2076, true, ManifestSentinel);
		}

		private static bool ContainsSentinel(byte[] packet, byte[] sentinel)
		{
			for (var i = 0; i + sentinel.Length <= packet.Length; i++)
			{
				var match = true;
				for (var j = 0; j < sentinel.Length; j++)
				{
					if (packet[i + j] != sentinel[j])
					{
						match = false;
						break;
					}
				}

				if (match)
					return true;
			}

			return false;
		}

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
			}
			while (v != 0);

			var header = new byte[ReceiverMarker.Length + varint.Count];
			ReceiverMarker.CopyTo(header, 0);
			varint.CopyTo(header, ReceiverMarker.Length);
			return header;
		}
	}
}
