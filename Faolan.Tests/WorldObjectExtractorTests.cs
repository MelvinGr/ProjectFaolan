using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Faolan.Importer.Extractors;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// Locks the world-object extraction against real captured bodies: the current map is tracked, and each object
	// yields its sub-opcode (class), id and name lifted from the object message.
	public class WorldObjectExtractorTests
	{
		[Fact]
		public void Extract_IgnoresObjectsBeforeAnyMapChange()
		{
			Assert.Empty(WorldObjectExtractor.Extract(new List<CapturedPacket> { RawObject(Golden.Bytes("WorldObjZone")) }));
		}

		// Decor (66AEDD50) is identified by name only (its message id is 0); the container/zone carry a numeric id.
		[Theory, InlineData("WorldObjDecor", 0x66AEDD50u, "Broken Bottle", 0u), InlineData("WorldObjChest", 0xF096251Eu, "Chest [N7FU]", 3662806u), InlineData("WorldObjZone", 0x33A56FB0u, "City of Tortage", 196787u)]
		public void Extract_LiftsClassIdAndName(string golden, uint subOpcode, string name, uint objectId)
		{
			var packets = new List<CapturedPacket>
			{
				MapChange(4010),
				RawObject(Golden.Bytes(golden))
			};

			var entry = Assert.Single(WorldObjectExtractor.Extract(packets));

			Assert.Equal(4010u, entry.MapId);
			Assert.Equal(subOpcode, entry.SubOpcode);
			Assert.Equal(name, entry.Name);
			Assert.Equal(objectId, entry.ObjectId);
		}

		private static CapturedPacket MapChange(uint mapId)
		{
			var content = new byte[38];
			BinaryPrimitives.WriteUInt32BigEndian(content.AsSpan(4, 4), 0x5D85BFC7); // body@4 = subop
			BinaryPrimitives.WriteUInt32BigEndian(content.AsSpan(34, 4), mapId); // body@34 = map
			var full = new byte[12 + content.Length]; // headerLen = 0
			content.CopyTo(full, 12);
			return new CapturedPacket(0, true, 0x2000, 0, full);
		}

		private static CapturedPacket RawObject(byte[] body)
		{
			var full = new byte[12 + body.Length]; // headerLen = 0
			body.CopyTo(full, 12);
			return new CapturedPacket(0, true, 0x2000, 0, full);
		}
	}
}
