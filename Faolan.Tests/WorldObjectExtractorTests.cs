using System;
using System.Collections.Generic;
using System.Numerics;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.World;
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
			Assert.Empty(WorldObjectExtractor.Extract([RawObject(Golden.Bytes("WorldObjZone"))]));
		}

		// Decor (66AEDD50) is identified by name only (its message id is 0); the container/zone carry a numeric id.
		[Theory, InlineData("WorldObjDecor", 0x66AEDD50u, "Broken Bottle", 0u), InlineData("WorldObjChest", 0xF096251Eu, "Chest [N7FU]", 3662806u),
		 InlineData("WorldObjZone", 0x33A56FB0u, "City of Tortage", 196787u)]
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

		// #363: a LootBagFullUpdateIIR object is kept ONLY when it carries a world-builder [script-tag] (a PLACED
		// container); every tagless one is a transient DROP - including boss/raid chests named exactly like a placed
		// one, which is why keying on the "chest" name wrongly baked the "Large Chest" drops.
		[Theory, InlineData("Chest [N7FU]", false), InlineData("aquilonian_treasure_chest [QRON]", false), InlineData("Large Chest", true), InlineData("Chest", true), InlineData("Bag of loot", true)]
		public void IsTransientLootContainer_KeepsOnlyScriptTaggedPlacements(string name, bool transient)
		{
			Assert.Equal(transient, WorldObjectExtractor.IsTransientLootContainer(name));
		}

		// A REAL PlayfieldFIIR, built by the codec: PlayfieldFCodec.Parse reads the record to its last byte (#530
		// rule 3), so a synthetic body with the map id at the right offset is not a MapChange any more.
		private static CapturedPacket MapChange(uint mapId)
		{
			var record = PlayfieldFCodec.Build(Vector3.Zero, mapId, 0, 1, 0x01EA7B9C, 15f, 13.86f, 0);
			var body = new byte[4 + record.Length];
			body.WriteU32Be(0, (uint)record.Length);
			record.CopyTo(body, 4);
			return Packet(body);
		}

		private static CapturedPacket RawObject(byte[] body)
		{
			return Packet(body);
		}

		// The sub-opcode is body[4:8], exactly as TsharkCaptureReader derives it from a real dump.
		private static CapturedPacket Packet(byte[] body)
		{
			var full = new byte[12 + body.Length]; // headerLen = 0
			body.CopyTo(full, 12);
			return new CapturedPacket(0, true, 0x2000, body.Length >= 8 ? body.ReadU32Be(4) : 0, full);
		}
	}
}
