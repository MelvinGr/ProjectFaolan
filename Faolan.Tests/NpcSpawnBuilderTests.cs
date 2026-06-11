using System;
using System.Buffers.Binary;
using Faolan.Core.Database.Tables;
using Faolan.GameServer.Spawn;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// Locks the DB-row -> wire-body bridge: building an NpcSpawn whose columns + Data come from a capture
	// reproduces the captured FA015807 body byte-for-byte (addressed to its own handle), and re-addressing to a
	// new session handle changes only the handle field.
	public class NpcSpawnBuilderTests
	{
		[Theory, InlineData("SpawnNpcCrocodile"), InlineData("SpawnNpcCasilda")]
		public void TryBuild_FromCapturedRow_ReproducesCapturedBody(string golden)
		{
			var capturedBody = Golden.Bytes(golden);
			var row = RowFromCapturedBody(capturedBody);
			var handle = BinaryPrimitives.ReadUInt32BigEndian(capturedBody.AsSpan(SpawnPacketBuilder.HandleOffset, 4));

			Assert.True(NpcSpawnBuilder.TryBuild(row, handle, out var body));
			Assert.Equal(capturedBody, body);
		}

		[Fact]
		public void TryBuild_ReaddressesHandle_OnlyHandleChanges()
		{
			var capturedBody = Golden.Bytes("SpawnNpcCrocodile");
			var row = RowFromCapturedBody(capturedBody);

			Assert.True(NpcSpawnBuilder.TryBuild(row, 0x000A1234u, out var body));

			Assert.Equal(capturedBody.Length, body.Length);
			Assert.Equal(0x000A1234u, BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(SpawnPacketBuilder.HandleOffset, 4)));
			for (var i = 0; i < body.Length; i++)
			{
				var inHandleField = i >= SpawnPacketBuilder.HandleOffset && i < SpawnPacketBuilder.HandleOffset + 4;
				if (!inHandleField)
					Assert.Equal(capturedBody[i], body[i]); // nothing outside the handle field changed
			}
		}

		private static float ReadLE(byte[] b, int offset)
		{
			return BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(offset, 4));
		}

		// Builds an NpcSpawn row the way the importer does: Data = the full captured packet (12-byte zero header
		// wrapping the body), transform columns read from the captured body's transform offsets.
		private static NpcSpawn RowFromCapturedBody(byte[] body)
		{
			var data = new byte[12 + body.Length]; // headerLen = 0
			body.CopyTo(data, 12);
			return new NpcSpawn
			{
				Data = data,
				PositionX = ReadLE(body, SpawnPacketBuilder.PosXOffset),
				PositionY = ReadLE(body, SpawnPacketBuilder.PosYOffset),
				PositionZ = ReadLE(body, SpawnPacketBuilder.PosZOffset),
				RotationX = ReadLE(body, SpawnPacketBuilder.RotXOffset),
				RotationY = ReadLE(body, SpawnPacketBuilder.RotYOffset),
				RotationZ = ReadLE(body, SpawnPacketBuilder.RotZOffset),
				RotationW = ReadLE(body, SpawnPacketBuilder.RotWOffset)
			};
		}
	}
}
