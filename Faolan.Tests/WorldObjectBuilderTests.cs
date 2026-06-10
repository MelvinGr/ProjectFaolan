using System;
using System.Buffers.Binary;
using Faolan.Core.Database.Tables;
using Faolan.GameServer.Spawn;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// Locks the static-world-object construction: building a WorldObject whose columns + Data come from a capture
	// reproduces the captured body byte-for-byte (addressed to its own handle, sub-opcode preserved), across all
	// three classes (decor 66AEDD50, container F096251E, zone 33A56FB0). Re-addressing changes only the handle.
	public class WorldObjectBuilderTests
	{
		[Theory]
		[InlineData("WorldObjDecor")]
		[InlineData("WorldObjChest")]
		[InlineData("WorldObjZone")]
		public void TryBuild_FromCapturedRow_ReproducesCapturedBody(string golden)
		{
			var capturedBody = Golden.Bytes(golden);
			var row = RowFromCapturedBody(capturedBody);
			var handle = BinaryPrimitives.ReadUInt32BigEndian(capturedBody.AsSpan(SpawnPacketBuilder.HandleOffset, 4));

			Assert.True(WorldObjectBuilder.TryBuild(row, handle, out var body));
			Assert.Equal(capturedBody, body);
		}

		[Fact]
		public void TryBuild_ReaddressesHandle_OnlyHandleChanges()
		{
			var capturedBody = Golden.Bytes("WorldObjChest");
			var row = RowFromCapturedBody(capturedBody);

			Assert.True(WorldObjectBuilder.TryBuild(row, 0x000B5678u, out var body));

			Assert.Equal(capturedBody.Length, body.Length);
			Assert.Equal(0x000B5678u, BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(SpawnPacketBuilder.HandleOffset, 4)));
			for (var i = 0; i < body.Length; i++)
			{
				var inHandleField = i >= SpawnPacketBuilder.HandleOffset && i < SpawnPacketBuilder.HandleOffset + 4;
				if (!inHandleField)
					Assert.Equal(capturedBody[i], body[i]);
			}
		}

		private static WorldObject RowFromCapturedBody(byte[] body)
		{
			var data = new byte[12 + body.Length]; // headerLen = 0
			body.CopyTo(data, 12);
			return new WorldObject
			{
				Data = data,
				SubOpcode = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(4, 4)),
				PositionX = ReadLE(body, SpawnPacketBuilder.PosXOffset),
				PositionY = ReadLE(body, SpawnPacketBuilder.PosYOffset),
				PositionZ = ReadLE(body, SpawnPacketBuilder.PosZOffset),
				RotationX = ReadLE(body, SpawnPacketBuilder.RotXOffset),
				RotationY = ReadLE(body, SpawnPacketBuilder.RotYOffset),
				RotationZ = ReadLE(body, SpawnPacketBuilder.RotZOffset),
				RotationW = ReadLE(body, SpawnPacketBuilder.RotWOffset)
			};
		}

		private static float ReadLE(byte[] b, int offset)
		{
			return BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(offset, 4));
		}
	}
}
