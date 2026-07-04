using Faolan.Core.Database.Tables;
using Faolan.Core.Network;
using Faolan.GameServer;
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

		private static readonly byte[] CapturedGameTimeBody =
		[
			0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x92, 0x0C, 0x6A, 0x02, 0x4E, 0x93, 0x3F, 0x80, 0x00, 0x00
		];

		private static readonly byte[] CapturedMapChangeBody =
		[
			0x00, 0x43, 0x11, 0x4F, 0xB3, 0x44, 0x30, 0x39, 0x9A, 0x62, 0x00, 0x00, 0xC7, 0x9C, 0x00, 0x00,
			0x0F, 0xAA, 0x00, 0x00, 0x00, 0x00, 0x02, 0x16, 0x5E, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
			0x00, 0x02, 0x00, 0x00, 0x9C, 0x50, 0x00, 0x4F, 0xE4, 0x28, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00,
			0x00, 0x00, 0x18, 0x00, 0x00, 0x00, 0x01, 0x00, 0x4F, 0xE4, 0x28, 0x00, 0x00, 0xCB, 0x20, 0x41,
			0x70, 0x00, 0x00, 0x40, 0xE3, 0x94, 0x4F, 0x00, 0x00, 0x00, 0x03, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
			0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x07, 0x00, 0x00, 0x00, 0x00
		];

		[Fact]
		public void BuildGameTime_ReproducesCapturedRecord()
		{
			var record = IirRecordCodec.Parse(GameServerListener.BuildGameTime(BeachClient()), 0);

			Assert.Equal(IirRecordCodec.IirOpcode, record.Opcode);
			Assert.Equal(IirMessageTypes.GameTime, record.IirType);
			Assert.Equal(0u, record.Handle);
			Assert.Equal(0x01472BDDu, record.FieldA);
			Assert.Equal(CapturedGameTimeBody, record.Body);
		}

		[Fact]
		public void BuildMapChange_ReproducesCapturedRecord()
		{
			var record = IirRecordCodec.Parse(GameServerListener.BuildMapChange(BeachClient()), 0);

			Assert.Equal(IirRecordCodec.IirOpcode, record.Opcode);
			Assert.Equal(IirMessageTypes.MapChange, record.IirType);
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
				MapId = BeachMapId,
				PositionX = spawn.X,
				PositionY = spawn.Y,
				PositionZ = spawn.Z
			};
			return client;
		}
	}
}
