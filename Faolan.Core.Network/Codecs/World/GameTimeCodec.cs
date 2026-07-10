using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.World
{
	// GameTimeIIR - the world clock. Global (no map/player data); the client advances it locally, so a fixed
	// snapshot is emitted. Body = [subop][TypeTag=0][Handle=0][FieldA][17-byte time body]. See
	// GameServerListener.BuildGameTime. TODO: derive the time fields from the server clock for a live time-of-day.
	public static class GameTimeCodec
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.GameTimeIIR;

		public static byte[] Build()
		{
			return new FaolanStream()
				.WriteUInt32(Opcode)
				.WriteUInt32(0x00000000) // TypeTag
				.WriteUInt32(0x00000000) // Handle (global)
				.WriteUInt32(0x01472BDD) // FieldA
				// --- body (17 bytes) ---
				.WriteUInt32(0x00000000)
				.WriteByte(0x01)
				.WriteUInt64(0x0001920C6A024E93)
				.WriteUInt32(0x3F800000) // float 1.0 (time rate)
				.ToArray();
		}
	}
}
