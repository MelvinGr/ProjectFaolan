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
	// transform-with-token + identity-from-columns + KdTreeTail - across every class (decor 66AEDD50, container
	// F096251E, zone 33A56FB0, destructible 6BD56736, climbing dynel 1FD6412B). Re-addressing changes only the handle.
	public class WorldObjectBuilderTests
	{
		[Theory, InlineData("WorldObjDecor"), InlineData("WorldObjChest"), InlineData("WorldObjZone"), InlineData("WorldObjDestructible"), InlineData("WorldObjClimbing")]
		public void TryBuild_FromLiftedRow_ReproducesCapturedBody(string golden)
		{
			var capturedBody = Golden.Bytes(golden);
			var row = RowFromCapturedBody(capturedBody);
			var handle = BinaryPrimitives.ReadUInt32BigEndian(capturedBody.AsSpan(SpawnPacketBuilder.HandleOffset, 4));

			Assert.True(WorldObjectBuilder.TryBuild(row, handle, ReadToken(capturedBody), out var body));
			Assert.Equal(capturedBody, body);
		}

		[Fact]
		public void TryBuild_ReaddressesHandle_OnlyHandleChanges()
		{
			var capturedBody = Golden.Bytes("WorldObjChest");
			var row = RowFromCapturedBody(capturedBody);

			Assert.True(WorldObjectBuilder.TryBuild(row, 0x000B5678u, ReadToken(capturedBody), out var body));

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
			// Lift the identity columns + geometry tail from the captured body (as the importer does from the dump).
			Assert.True(SimpleDynelMessageCodec.TryLiftColumns(row, body), $"lift failed for {row.SubOpcode:X8}");
			return row;
		}
	}
}
