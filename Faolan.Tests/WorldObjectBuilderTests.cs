using System;
using System.Buffers.Binary;
using Faolan.Core.Database.Tables;
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
	public class WorldObjectBuilderTests
	{
		[Theory, InlineData("WorldObjDecor"), InlineData("WorldObjZone"), InlineData("WorldObjDestructible"), InlineData("WorldObjClimbing")]
		public void TryBuild_FromLiftedRow_ReproducesCapturedBody(string golden)
		{
			var capturedBody = Golden.Bytes(golden);
			var row = RowFromCapturedBody(capturedBody);
			var handle = BinaryPrimitives.ReadUInt32BigEndian(capturedBody.AsSpan(SpawnPacketBuilder.HandleOffset, 4));

			var body = WorldObjectBuilder.Build(row, handle, ReadToken(capturedBody));
			Assert.Equal(capturedBody, body);
		}

		// The container: byte-exact up to the loot contents, which are not DB state (see the class comment). The
		// prefix length is the captured body minus its contents, so this still pins the header, transform, identity
		// and the whole geometry record.
		[Fact]
		public void TryBuild_FromLiftedChest_ReproducesCapturedBody_UpToTheLootContents()
		{
			var capturedBody = Golden.Bytes("WorldObjChest");
			var row = RowFromCapturedBody(capturedBody);
			var handle = BinaryPrimitives.ReadUInt32BigEndian(capturedBody.AsSpan(SpawnPacketBuilder.HandleOffset, 4));

			var body = WorldObjectBuilder.Build(row, handle, ReadToken(capturedBody));

			var contentsStart = body.Length - LootContainerTemplate.BuildEmptyContents().Length;
			Assert.True(contentsStart > 0 && contentsStart < capturedBody.Length, $"contents boundary {contentsStart} is not inside the captured body ({capturedBody.Length})");

			// From byte 4: the body-length word at [0] legitimately differs, since an empty contents scaffold is
			// shorter than the captured chest's loot. Everything the columns own must still match to the byte.
			Assert.Equal(capturedBody[4..contentsStart], body[4..contentsStart]);
			Assert.Equal((uint)(body.Length - 4), BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(0, 4)));
		}

		[Fact]
		public void TryBuild_ReaddressesHandle_OnlyHandleChanges()
		{
			// A decor object, not the chest: its body is reproduced to the last byte, so "only the handle changed" is
			// a meaningful whole-body claim.
			var capturedBody = Golden.Bytes("WorldObjDecor");
			var row = RowFromCapturedBody(capturedBody);

			var body = WorldObjectBuilder.Build(row, 0x000B5678u, ReadToken(capturedBody));

			Assert.Equal(capturedBody.Length, body.Length);
			Assert.Equal(0x000B5678u, BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(SpawnPacketBuilder.HandleOffset, 4)));
			for (var i = 0; i < body.Length; i++)
			{
				var inHandleField = i is >= SpawnPacketBuilder.HandleOffset and < SpawnPacketBuilder.HandleOffset + 4;
				if (!inHandleField)
					Assert.Equal(capturedBody[i], body[i]);
			}
		}

		private static float ReadLE(byte[] b, int offset)
		{
			return BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(offset, 4));
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
				SubOpcode = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(4, 4)),
				PositionX = ReadLE(body, SpawnPacketBuilder.PosXOffset),
				PositionY = ReadLE(body, SpawnPacketBuilder.PosYOffset),
				PositionZ = ReadLE(body, SpawnPacketBuilder.PosZOffset),
				RotationX = ReadLE(body, SpawnPacketBuilder.RotXOffset),
				RotationY = ReadLE(body, SpawnPacketBuilder.RotYOffset),
				RotationZ = ReadLE(body, SpawnPacketBuilder.RotZOffset),
				RotationW = ReadLE(body, SpawnPacketBuilder.RotWOffset)
			};
			// Lift the identity + geometry columns from the captured body (as the importer does from the dump).
			Assert.True(SimpleDynelMessageCodec.TryLiftColumns(row, body), $"lift failed for {row.SubOpcode:X8}");
			return row;
		}
	}
}
