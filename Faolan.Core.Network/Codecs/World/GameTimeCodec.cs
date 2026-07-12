using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.World
{
	// GameTimeIIR ("SetDayTime") - the world clock that drives the day/night cycle. Global (no map/player data); the
	// client advances it locally at `rate`, so the server emits a fixed captured snapshot. Record =
	// [subop][TypeTag=0][Handle=0][u64 dayTime][u8 flag][u64 timestamp][f32 rate] - the same 29-byte record
	// CreateEditorRecordBuilder.BuildGameTimeIir builds (see GameServerListener.BuildGameTime). dayTime is the game
	// clock the sun/lighting reads off (low word 0); rate=1.0 = base speed. A live day/night cycle (advance dayTime
	// at rate) is #351 - blocked on pinning dayTime's unit + the cycle length in-client.
	public static class GameTimeCodec
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.GameTimeIIR;

		public static byte[] Build()
		{
			return new FaolanStream()
				.WriteUInt32(Opcode)
				.WriteUInt32(0x00000000) // TypeTag
				.WriteUInt32(0x00000000) // Handle (global)
				.WriteUInt64(0x01472BDD00000000) // dayTime (game clock)
				.WriteByte(0x01) // flag
				.WriteUInt64(0x0001920C6A024E93) // timestamp (resync anchor)
				.WriteUInt32(0x3F800000) // rate = 1.0f
				.ToArray();
		}
	}
}
