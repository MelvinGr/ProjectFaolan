using System;
using System.Buffers.Binary;
using System.Numerics;
using Faolan.GameServer.Economy;
using Faolan.GameServer.Spawn;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// #249: a loot bag built FROM SCRATCH (the baked canonical LootBagTemplate columns + embedded KdTreeTail, via
	// WorldObjectBuilder.BuildFromScratch) reproduces a captured "Bag of loot" body byte-for-byte - so the loot system
	// needs NO captured loot-bag WorldObject row in the world (GetLootTemplate is map-independent).
	public class LootBagTemplateTests
	{
		[Fact]
		public void BuildFromScratch_FromCanonicalTemplate_ReproducesCapturedBag()
		{
			var golden = Golden.Bytes("LootBagBody");
			var handle = BinaryPrimitives.ReadUInt32BigEndian(golden.AsSpan(SpawnPacketBuilder.HandleOffset, 4));
			var typeTag = BinaryPrimitives.ReadUInt32BigEndian(golden.AsSpan(8, 4)); // the loot typetag (0xC87A)
			var pos = new Vector3(ReadLE(golden, SpawnPacketBuilder.PosXOffset), ReadLE(golden, SpawnPacketBuilder.PosYOffset), ReadLE(golden, SpawnPacketBuilder.PosZOffset));
			var rot = new Quaternion(ReadLE(golden, SpawnPacketBuilder.RotXOffset), ReadLE(golden, SpawnPacketBuilder.RotYOffset), ReadLE(golden, SpawnPacketBuilder.RotZOffset), ReadLE(golden, SpawnPacketBuilder.RotWOffset));
			const uint playfieldToken = 0x01EA9852; // the captured bag's transform playfield varint (d2 b0 aa 0f)

			Assert.True(LootBagTemplate.IsAvailable);
			Assert.True(WorldObjectBuilder.BuildFromScratch(LootBagTemplate.Create(), handle, pos, rot, typeTag, playfieldToken, out var body));

			Assert.Equal(golden, body);
		}

		private static float ReadLE(byte[] b, int offset)
		{
			return BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(offset, 4));
		}
	}
}
