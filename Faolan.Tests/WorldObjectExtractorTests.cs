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

		// #554: `IsClosed` is the one identity field that changes during play - the map 4020 "Wharf Door" is captured
		// closed in one session and open (the field ABSENT) in another - so first-seen made ordinal capture FILENAME
		// order decide a door's world-entry state. The value is really session state and should come from the live
		// world (#598); until it does, the merge must at least reach the same answer whatever order the captures
		// arrive in, which is what the reversed case asserts. The all-null case is the sanity check that being
		// order-independent is not just "it always picks the same slot".
		[Theory, InlineData(true, null, true), InlineData(null, true, true), InlineData(null, null, null), InlineData(false, null, false), InlineData(true, false, true)]
		public void Merge_ResolvesTheOpenClosedStateByWhatTheEntrySaysNotByCaptureOrder(bool? first, bool? second, bool? expected)
		{
			var entries = new List<WorldObjectEntry>
			{
				Door(first),
				Door(second)
			};

			var merged = Assert.Single(WorldObjectExtractor.Merge(entries, out var conflicts));
			Assert.Equal(expected, merged.IsClosed);
			Assert.Equal(first == second ? 0 : 1, conflicts);

			// The same two entries the other way round - the whole point of the ticket.
			entries.Reverse();
			Assert.Equal(expected, Assert.Single(WorldObjectExtractor.Merge(entries)).IsClosed);
		}

		// Two entries that are the SAME object by the dedup key (map, spawn point, display identity) and differ only
		// in the state field.
		private static WorldObjectEntry Door(bool? isClosed)
		{
			return new WorldObjectEntry(4020, 0x66AEDD50, 0, 1f, 2f, 3f, [])
			{
				DisplayName = "Wharf Door",
				IsClosed = isClosed
			};
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
