using System;
using System.Numerics;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.World;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// #193: a loot bag built ENTIRELY FROM SCRATCH - identity columns + the DECODED kd-tree bounds (typed,
	// LootContainerTemplate.BuildBounds) + a clean codec-built ProtoLootBagIIR scaffold - reproduces a captured "Bag of loot"
	// body's identity + geometry byte-for-byte, with no embedded asset and no baked capture state (the former
	// LootBagKdTreeTail.bin carried the captured bag's stale OwnerId + a captured item). The live items + cash are
	// spliced per drop (WithLootContents), so the at-rest template carries an EMPTY scaffold.
	public class LootBagTemplateTests
	{
		[Fact]
		public void BuildBounds_ReproducesTheCapturedKdTreeGeometry_ByteForByte()
		{
			var record = Golden.Bytes("LootBagBody").AsSpan(4).ToArray(); // the carrier's [u32 len] is the sender's (#530 rule 2)
			Assert.True(WorldObjectColumns.TryLocateContents(record, out _, out var contentsStart));

			// The kd-tree bounds are the bytes immediately before the trailing [u32 len][ProtoLootBagIIR] content.
			var bounds = LootContainerTemplate.BuildBounds();
			var tail = WorldObjectCodec.Parse(record).Tail;
			var capturedBounds = tail.AsSpan(contentsStart - bounds.Length, bounds.Length).ToArray();
			Assert.Equal(capturedBounds, bounds);
		}

		[Fact]
		public void BuildFromScratch_ReproducesIdentityAndGeometry_WithCleanScaffold()
		{
			var golden = Golden.Bytes("LootBagBody");
			var record = golden.AsSpan(4).ToArray();
			var handle = golden.ReadU32Be(SpawnPacketBuilder.HandleOffset);
			var typeTag = golden.ReadU32Be(8); // the loot typetag (0xC87A)
			var pos = new Vector3(golden.ReadF32Le(SpawnPacketBuilder.PosXOffset), golden.ReadF32Le(SpawnPacketBuilder.PosYOffset), golden.ReadF32Le(SpawnPacketBuilder.PosZOffset));
			var rot = new Quaternion(golden.ReadF32Le(SpawnPacketBuilder.RotXOffset), golden.ReadF32Le(SpawnPacketBuilder.RotYOffset), golden.ReadF32Le(SpawnPacketBuilder.RotZOffset),
				golden.ReadF32Le(SpawnPacketBuilder.RotWOffset));
			const uint playfieldToken = 0x01EA9852; // the captured bag's transform playfield varint (d2 b0 aa 0f)

			Assert.True(WorldObjectColumns.TryLocateContents(record, out _, out var contentsStart));
			var body = WorldObjectCodec.Build(LootContainerTemplate.Create(LootContainerTemplateId.Bag), handle, pos, rot, typeTag, playfieldToken);

			// Identity message + kd-tree geometry (everything up to the content blob) are byte-identical to the capture.
			// (The content blob differs: the from-scratch bag carries a clean EMPTY scaffold, not the captured bag's
			// stale owner + item.)
			Assert.Equal(WorldObjectCodec.Parse(record).Tail.AsSpan(0, contentsStart).ToArray(), WorldObjectCodec.Parse(body).Tail.AsSpan(0, contentsStart).ToArray());

			// The scaffold is a well-formed, EMPTY loot bag with no baked owner.
			Assert.True(WorldObjectColumns.TryLocateContents(body, out var contents, out _));
			Assert.Empty(contents.Inventory.Items);
			Assert.Equal(0u, contents.OwnerId.Instance);
		}

	}
}
