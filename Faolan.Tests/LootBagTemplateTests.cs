using System;
using System.Buffers.Binary;
using System.Numerics;
using Faolan.Core.Network.Codecs;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// #193: a loot bag built ENTIRELY FROM SCRATCH - identity columns + the DECODED kd-tree bounds (typed,
	// LootBagTemplate.BuildBounds) + a clean codec-built ProtoLootBagIIR scaffold - reproduces a captured "Bag of loot"
	// body's identity + geometry byte-for-byte, with no embedded asset and no baked capture state (the former
	// LootBagKdTreeTail.bin carried the captured bag's stale OwnerId + a captured item). The live items + cash are
	// spliced per drop (WithLootContents), so the at-rest template carries an EMPTY scaffold.
	public class LootBagTemplateTests
	{
		[Fact]
		public void BuildBounds_ReproducesTheCapturedKdTreeGeometry_ByteForByte()
		{
			var golden = Golden.Bytes("LootBagBody");
			Assert.True(LootBagMessageCodec.TryParse(golden.AsSpan(8), out _, out var contentsStart));

			// The kd-tree bounds are the bytes immediately before the trailing [u32 len][ProtoLootBagIIR] content.
			var bounds = LootBagTemplate.BuildBounds();
			var capturedBounds = golden.AsSpan(8 + contentsStart - bounds.Length, bounds.Length).ToArray();
			Assert.Equal(capturedBounds, bounds);
		}

		[Fact]
		public void BuildFromScratch_ReproducesIdentityAndGeometry_WithCleanScaffold()
		{
			var golden = Golden.Bytes("LootBagBody");
			var handle = BinaryPrimitives.ReadUInt32BigEndian(golden.AsSpan(SpawnPacketBuilder.HandleOffset, 4));
			var typeTag = BinaryPrimitives.ReadUInt32BigEndian(golden.AsSpan(8, 4)); // the loot typetag (0xC87A)
			var pos = new Vector3(ReadLE(golden, SpawnPacketBuilder.PosXOffset), ReadLE(golden, SpawnPacketBuilder.PosYOffset), ReadLE(golden, SpawnPacketBuilder.PosZOffset));
			var rot = new Quaternion(ReadLE(golden, SpawnPacketBuilder.RotXOffset), ReadLE(golden, SpawnPacketBuilder.RotYOffset), ReadLE(golden, SpawnPacketBuilder.RotZOffset), ReadLE(golden, SpawnPacketBuilder.RotWOffset));
			const uint playfieldToken = 0x01EA9852; // the captured bag's transform playfield varint (d2 b0 aa 0f)

			Assert.True(LootBagTemplate.IsAvailable);
			Assert.True(LootBagMessageCodec.TryParse(golden.AsSpan(8), out _, out var contentsStart));
			Assert.True(WorldObjectBuilder.BuildBody(LootBagTemplate.Create(), handle, pos, rot, typeTag, playfieldToken, out var body));

			// Identity message + kd-tree geometry (everything up to the content blob) are byte-identical to the capture.
			// (The carrier length at [0..4] and the content blob differ: the from-scratch bag carries a clean EMPTY
			// scaffold, not the captured bag's stale owner + item.)
			Assert.Equal(golden.AsSpan(8, contentsStart).ToArray(), body.AsSpan(8, contentsStart).ToArray());

			// The scaffold is a well-formed, EMPTY loot bag with no baked owner.
			Assert.True(LootBagMessageCodec.TryParse(body.AsSpan(8), out var contents, out _));
			Assert.Empty(contents.Inventory.Items);
			Assert.Equal(0u, contents.OwnerId.Instance);
		}

		private static float ReadLE(byte[] b, int offset)
		{
			return BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(offset, 4));
		}
	}
}
