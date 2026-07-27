using System;
using Faolan.Core.Database.Interfaces;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.GameServer;
using Faolan.GameServer.World;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// Locks the server-generated world-entry records (the globals/player-state moved out of the DB burst) against
	// their captured form: a record built with the captured map+instance must reproduce the captured IIR body exactly.
	// The framing (RpcHeader + 0x2000) comes from the proven WriteHeader path; these assert the field-by-field body.
	public class WorldEntryGeneratedRecordsTests
	{
		private const uint BeachMapId = 0x00000FAA;
		private const uint StriePlayerInstance = 0x02165EFF;

		private static readonly byte[] CapturedGameTimeBody = [0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x92, 0x0C, 0x6A, 0x02, 0x4E, 0x93, 0x3F, 0x80, 0x00, 0x00];

		private static readonly byte[] CapturedMapChangeBody =
		[
			0x00, 0x43, 0x11, 0x4F, 0xB3, 0x44, 0x30, 0x39, 0x9A, 0x62, 0x00, 0x00, 0xC7, 0x9C, 0x00, 0x00, 0x0F, 0xAA, 0x00, 0x00, 0x00, 0x00, 0x02, 0x16, 0x5E, 0xFF, 0x00, 0x00, 0x00, 0x00,
			0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x9C, 0x50, 0x00, 0x4F, 0xE4, 0x28, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x18, 0x00, 0x00, 0x00, 0x01, 0x00, 0x4F, 0xE4, 0x28, 0x00,
			0x00, 0xCB, 0x20, 0x41, 0x70, 0x00, 0x00, 0x40, 0xE3, 0x94, 0x4F, 0x00, 0x00, 0x00, 0x03, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x07, 0x00, 0x00, 0x00, 0x00
		];

		[Fact]
		public void BuildGameTime_ReproducesCapturedRecord()
		{
			// Both clock fields are generated now (#351), so feed the capture's own values back: the instant it was taken
			// (its resync anchor read as unix epoch seconds) and the time of day it was showing. The record must then
			// rebuild byte-for-byte - what this pins is the LAYOUT; the time of day itself is server state.
			var record = DecodeIir(GameServerListener.BuildGameTime(BeachClient(), BitConverter.UInt32BitsToSingle(0x472BDD00), DateTimeOffset.FromUnixTimeSeconds(0x6A024E93)).Serialize());

			Assert.Equal(0x2000u, record.Opcode);
			Assert.Equal((uint)GameServer0X2000Opcodes.GameTimeIIR, record.IirType);
			Assert.Equal(0u, record.Handle);
			// NB "FieldA" is this decoder's generic name for the 4 bytes at that offset, and under the corrected clock
			// split (#351) they are not one field: 0x01 is the isToBePassed bool and 0x472BDD are the top 3 bytes of the
			// f32 dayTime. The value is still the right assertion - the captured bytes did not change, only their reading.
			Assert.Equal(0x01472BDDu, record.FieldA);
			Assert.Equal(CapturedGameTimeBody, record.Body);
		}

		[Fact]
		public void BuildMapChange_ReproducesCapturedRecord()
		{
			// map=null -> BuildMapChange uses the beach defaults (instanced, count 2, fieldB 3, flags 0), reproducing the beach capture.
			var record = DecodeIir(GameServerListener.BuildMapChange(BeachClient(), null!, WorldState.DefaultPlayfieldToken).Serialize());

			Assert.Equal(0x2000u, record.Opcode);
			Assert.Equal((uint)GameServer0X2000Opcodes.PlayfieldFIIR, record.IirType);
			Assert.Equal(0u, record.Handle);
			Assert.Equal(0x0043A140u, record.FieldA);
			Assert.Equal(CapturedMapChangeBody, record.Body);
		}

		private static RecordingNetworkClient BeachClient()
		{
			var client = new RecordingNetworkClient(StriePlayerInstance);
			// BuildMapChange now writes the character's real entry position; the captured golden bytes are the Tortage
			// Beach create-spawn, so set it from that single source of truth.
			var spawn = Map.NewPlayerSpawnSpawnPoint.Position;
			client.Session.Character = new Character
			{
				Id = StriePlayerInstance,
				MapId = BeachMapId,
				PositionX = spawn.X,
				PositionY = spawn.Y,
				PositionZ = spawn.Z
			};
			return client;
		}

		// Decodes a generated 0x2000 IIR download packet into its body fields. The framing (RpcHeader + opcode) is the
		// self-describing length at offset 8 (body starts at 12 + that); the IIR body is [u32 bodyLen][u32 IirType]
		// [u32 TypeTag][u32 Handle][u32 FieldA][payload]. Test-local so these assert the generated body field-by-field.
		private static (uint Opcode, uint IirType, uint Handle, uint FieldA, byte[] Body) DecodeIir(byte[] packet)
		{
			var bodyStart = 12 + (int)packet.ReadU32Be(8);
			return (packet.ReadU16Be(bodyStart - 2), packet.ReadU32Be(bodyStart + 4),
				packet.ReadU32Be(bodyStart + 12), packet.ReadU32Be(bodyStart + 16), packet[(bodyStart + 20)..]);
		}
	}
}
