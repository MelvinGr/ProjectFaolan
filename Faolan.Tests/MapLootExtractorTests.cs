using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Faolan.Importer.Extractors;
using Xunit;

namespace Faolan.Tests
{
	// Locks the per-map loot pool extraction (#273): a captured loot bag's items are lifted out of its
	// LootBagFullUpdateIIR (0xF096251E) and attributed to the map the preceding MapChange selected.
	public class MapLootExtractorTests
	{
		// The captured "Chest [N7FU]" loot-bag IIR record (after the [u32 len][u32 subOpcode] prefix) - the same bytes
		// LootBagMessageTests parses. It carries two items (templates 0x43109F43 / 0x4080127 in the ProtoAcgItem).
		private const string RecordHex =
			"0000c87a0000219f000000002c0a0f0d77861444150a772f431d8bcc6c4412140d6f0fc2bc1579e7d0bc1d63d4353c2539d47fbf18dcb0aa0f0000007d08d6c7df01120c4368657374205b4e3746555d1a613c72656d6f7465666f726d61742069643d223133373832222063617465676f72793d22353032303022206b65793d22212c3125565a6323292a63746b2d7a4e464e784922206b6e75626f743d22302220203e3c2f72656d6f7465666f726d61743e280140d66b60010037e452000f6a190000000d6f626a6563745f636c6f736564000000007f7fffee7f7fffee7f7fffeeff7fffeeff7fffeeff7fffee3f80000000000000000000011808001204080010001a04080010002204080010002a0408001000380042f9010a050873109f43100a180022750800127108b7e5d2e0b1ec8aec201204080010011204081a10011204082e10031a530a140d325b3cee15116db43c1dae637bc5250749266512140d325b3cee15116db43c1dae637bc525074926651a140d325b3cee15116db43c1dae637bc5250749266525d21535002dd21535003801453831464322750801127108b8e5d2e0b1ec8aec201204080010011204081a10011204082e10031a530a140df9ad8ee915cb73ec6e1dbd224735256066dff012140df9ad8ee915cb73ec6e1dbd224735256066dff01a140df9ad8ee915cb73ec6e1dbd224735256066dff025b38939002db38939003801454c374a45";

		[Fact]
		public void Extract_AttributesBagItemsToCurrentMap()
		{
			var packets = new List<CapturedPacket>
			{
				MapChange(4010),
				LootBag(Convert.FromHexString(RecordHex))
			};

			var entries = MapLootExtractor.Extract(packets);

			Assert.Equal(2, entries.Count); // the chest holds two items
			Assert.All(entries, e => Assert.Equal(4010u, e.MapId));
			Assert.Equal(2, entries.Select(e => e.ItemTemplateId).Distinct().Count());
		}

		[Fact]
		public void Extract_IgnoresBagsBeforeAnyMapChange()
		{
			Assert.Empty(MapLootExtractor.Extract(new List<CapturedPacket>
			{
				LootBag(Convert.FromHexString(RecordHex))
			}));
		}

		[Fact]
		public void Merge_DedupsRepeatedMapItemPairs()
		{
			var merged = MapLootExtractor.Merge([
				new MapLootPoolEntry(4010, 1), new MapLootPoolEntry(4010, 1), // duplicate (same map + item)
				new MapLootPoolEntry(4010, 2), new MapLootPoolEntry(4020, 1) // same item, different map - kept
			]);

			Assert.Equal(3, merged.Count);
		}

		// Prepends the 12-byte framing header (len + crc + headerLen=0) the body extractor skips.
		private static byte[] FramePacket(byte[] body)
		{
			var full = new byte[12 + body.Length]; // headerLen = 0 at offset 8
			body.CopyTo(full, 12);
			return full;
		}

		// A loot-bag carrier packet: body = [u32 payloadLen][u32 subOpcode=F096251E][record].
		private static CapturedPacket LootBag(byte[] record)
		{
			var payload = new byte[8 + record.Length];
			BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0, 4), (uint)(4 + record.Length)); // payload len
			BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4, 4), 0xF096251E); // sub-opcode
			record.CopyTo(payload, 8);
			return new CapturedPacket(0, true, 0x2000, 0xF096251E, FramePacket(payload));
		}

		private static CapturedPacket MapChange(uint mapId)
		{
			var content = new byte[38];
			BinaryPrimitives.WriteUInt32BigEndian(content.AsSpan(4, 4), 0x5D85BFC7); // body@4 = subop
			BinaryPrimitives.WriteUInt32BigEndian(content.AsSpan(34, 4), mapId); // body@34 = map
			return Wrap(0x5D85BFC7, content);
		}

		private static CapturedPacket Wrap(uint subOpcode, byte[] body)
		{
			return new CapturedPacket(0, true, 0x2000, subOpcode, FramePacket(body));
		}
	}
}
