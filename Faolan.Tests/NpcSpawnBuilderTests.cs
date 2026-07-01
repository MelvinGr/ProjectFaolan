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
		// The from-DB lift (#185): on a fresh spawn, a humanoid's post-IIR APPEARANCE table is regenerated from the
		// row's NpcSpawnMorph rows (not carried from Data). Fed the rows decoded from the capture, the rebuilt
		// post-IIR reproduces the captured appearance table byte-for-byte - and is NOT the bald minimal form.
		[Fact]
		public void TryBuild_FreshSpawn_HumanoidWithDbMorphs_RegeneratesCapturedAppearance()
		{
			var capturedBody = Golden.Bytes("SpawnNpcCasilda");
			Assert.True(PlayerSpawnReader.TryReadCharacterMessage(capturedBody, out var cap));
			var capturedPostIir = capturedBody[(cap.Offset + cap.Length)..];

			var row = RowFromCapturedBody(capturedBody);
			Assert.True(NpcSpawnAppearanceCodec.TryDecodeAppearance(capturedBody, out var morphs));
			for (var i = 0; i < morphs.Count; i++)
				row.Morphs.Add(new NpcSpawnMorph
				{
					Ordinal = i,
					MorphId = morphs[i].MorphId,
					Value = morphs[i].Value,
					Text = morphs[i].Text
				});

			var handle = BinaryPrimitives.ReadUInt32BigEndian(capturedBody.AsSpan(SpawnPacketBuilder.HandleOffset, 4));
			Assert.True(NpcSpawnBuilder.TryBuild(row, handle, out var body, true));

			Assert.True(PlayerSpawnReader.TryReadCharacterMessage(body, out var read));
			var builtPostIir = body[(read.Offset + read.Length)..];
			Assert.Equal(capturedPostIir, builtPostIir);
			Assert.NotEqual(NpcSpawnScaffold.BuildMinimalPostIir(), builtPostIir);
		}

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

		// The from-DB lift (#185): worn equipment is rebuilt from the row's NpcSpawnEquipment rows, not the Data
		// blob's f23. Fed the rows lifted from Casilda's capture, the rebuilt body reproduces it byte-for-byte.
		[Fact]
		public void TryBuild_WithDbEquipment_ReproducesCapturedBody()
		{
			var capturedBody = Golden.Bytes("SpawnNpcCasilda");
			Assert.True(PlayerSpawnReader.TryReadCharacterMessage(capturedBody, out var read));

			var row = RowFromCapturedBody(capturedBody);
			foreach (var entry in read.Message.InventoryEntries)
			{
				Assert.True(NpcSpawnEquipmentBuilder.TryLift(entry, out var equip));
				row.Equipment.Add(equip);
			}

			Assert.NotEmpty(row.Equipment);

			var handle = BinaryPrimitives.ReadUInt32BigEndian(capturedBody.AsSpan(SpawnPacketBuilder.HandleOffset, 4));
			Assert.True(NpcSpawnBuilder.TryBuild(row, handle, out var body));
			Assert.Equal(capturedBody, body);
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
