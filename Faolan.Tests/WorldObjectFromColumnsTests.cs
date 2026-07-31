using Faolan.Core.Database.Tables;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.World;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// Locks the static-world-object construction: a WorldObject whose identity/geometry columns are LIFTED from a
	// capture (the raw Data blob is retired) rebuilds the captured body byte-for-byte via TryBuild - header +
	// transform-with-token + identity-from-columns + geometry-from-columns - across every class (decor 66AEDD50,
	// container F096251E, zone 33A56FB0, destructible 6BD56736, climbing dynel 1FD6412B). Re-addressing changes only
	// the handle.
	//
	// The CONTAINER is the one class whose body is not compared to its last byte: a container's trailer is its loot
	// CONTENTS, which is session state and deliberately not persisted (#410) - a chest rebuilds with the same clean
	// empty scaffold a dropped bag gets, so the comparison covers everything the columns own and stops where the
	// contents begin. The contents codec has its own gates (LootBagTemplateTests).
	public class WorldObjectFromColumnsTests
	{
		[Theory, InlineData("WorldObjDecor"), InlineData("WorldObjZone"), InlineData("WorldObjDestructible"), InlineData("WorldObjClimbing")]
		public void TryBuild_FromLiftedRow_ReproducesCapturedBody(string golden)
		{
			var capturedBody = Golden.Bytes(golden);
			var row = RowFromCapturedBody(capturedBody);
			var handle = capturedBody.ReadU32Be(SpawnPacketBuilder.HandleOffset);

			// The carrier's [u32 len] is the SENDER's (#530 rule 2), so the codec is compared against the record.
			var body = WorldObjectCodec.Build(row, handle, ReadToken(capturedBody));
			Assert.Equal(capturedBody[4..], body);
		}

		// The container: byte-exact up to the loot contents, which are not DB state (see the class comment). The
		// prefix length is the captured body minus its contents, so this still pins the header, transform, identity
		// and the whole geometry record.
		[Fact]
		public void TryBuild_FromLiftedChest_ReproducesCapturedBody_UpToTheLootContents()
		{
			var capturedBody = Golden.Bytes("WorldObjChest");
			var row = RowFromCapturedBody(capturedBody);
			var handle = capturedBody.ReadU32Be(SpawnPacketBuilder.HandleOffset);

			var body = WorldObjectCodec.Build(row, handle, ReadToken(capturedBody));

			var contentsStart = body.Length - LootContainerTemplate.BuildEmptyContents().Length;
			Assert.True(contentsStart > 0 && contentsStart < capturedBody.Length, $"contents boundary {contentsStart} is not inside the captured body ({capturedBody.Length})");

			// Everything the columns own must match to the byte; only the contents scaffold differs, since an empty
			// one is shorter than the captured chest's loot.
			Assert.Equal(capturedBody[4..(4 + contentsStart)], body[..contentsStart]);
		}

		[Fact]
		public void TryBuild_ReaddressesHandle_OnlyHandleChanges()
		{
			// A decor object, not the chest: its body is reproduced to the last byte, so "only the handle changed" is
			// a meaningful whole-body claim.
			var capturedBody = Golden.Bytes("WorldObjDecor");
			var row = RowFromCapturedBody(capturedBody);

			var body = WorldObjectCodec.Build(row, 0x000B5678u, ReadToken(capturedBody));

			Assert.Equal(capturedBody.Length - 4, body.Length);
			Assert.Equal(0x000B5678u, body.ReadU32Be(SpawnPacketBuilder.HandleOffset - 4));
			for (var i = 0; i < body.Length; i++)
			{
				var inHandleField = i is >= SpawnPacketBuilder.HandleOffset - 4 and < SpawnPacketBuilder.HandleOffset;
				if (!inHandleField)
					Assert.Equal(capturedBody[i + 4], body[i]);
			}
		}


		// The playfield token: a 4-byte LEB128 varint at [0x18] right after the transform (offset 60).
		private static uint ReadToken(byte[] body)
		{
			var o = SpawnPacketBuilder.RotWOffset + 4;
			return (uint)((body[o + 1] & 0x7F) | ((body[o + 2] & 0x7F) << 7) | ((body[o + 3] & 0x7F) << 14) | (body[o + 4] << 21));
		}

		private static WorldObject RowFromCapturedBody(byte[] body)
		{
			var row = new WorldObject
			{
				SubOpcode = body.ReadU32Be(4),
				PositionX = body.ReadF32Le(SpawnPacketBuilder.PosXOffset),
				PositionY = body.ReadF32Le(SpawnPacketBuilder.PosYOffset),
				PositionZ = body.ReadF32Le(SpawnPacketBuilder.PosZOffset),
				RotationX = body.ReadF32Le(SpawnPacketBuilder.RotXOffset),
				RotationY = body.ReadF32Le(SpawnPacketBuilder.RotYOffset),
				RotationZ = body.ReadF32Le(SpawnPacketBuilder.RotZOffset),
				RotationW = body.ReadF32Le(SpawnPacketBuilder.RotWOffset)
			};
			// Lift the identity + geometry columns from the captured body (as the importer does from the dump).
			Assert.True(WorldObjectColumns.TryLiftColumns(row, WorldObjectColumns.RecordOf(body)), $"lift failed for {row.SubOpcode:X8}");
			return row;
		}
	}
}
