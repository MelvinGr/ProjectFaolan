using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.World
{
	// GameTimeIIR ("SetDayTime") - the world clock that drives the day/night cycle. Global (no map/player data); the
	// client advances it locally at `rate`, so the server emits a fixed captured snapshot. Record =
	// [subop][TypeTag=0][Handle=0][u64 dayTime][u8 flag][u64 timestamp][f32 rate]. The body is the same 29-byte record
	// CreateEditorRecordBuilder.BuildGameTimeIir builds, so we share that builder (#351 side-cleanup - one builder,
	// named fields) and just prepend the subop here. dayTime is the game clock the sun/lighting reads (its live part
	// is the HIGH word; low word 0); rate=1.0 = base speed. A live day/night cycle (advance dayTime at rate) is the
	// remaining #351 work, blocked on pinning dayTime's unit + the cycle length via an in-client experiment.
	public static class GameTimeCodec
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.GameTimeIIR;

		// The world-entry clock snapshot (captured). WorldDayTime's high word is the game clock; WorldResyncTimestamp
		// is the resync anchor (NOT read for time-of-day); the client extrapolates from here at ClockRate.
		private const ulong WorldDayTime = 0x01472BDD_00000000;
		private const byte ClockFlag = 0x01;
		private const ulong WorldResyncTimestamp = 0x0001920C_6A024E93;
		private const float ClockRate = 1.0f;

		public static byte[] Build()
		{
			return new FaolanStream()
				.WriteUInt32(Opcode)
				.WriteArray(CreateEditorRecordBuilder.BuildGameTimeIir(WorldDayTime, ClockFlag, WorldResyncTimestamp, ClockRate))
				.ToArray();
		}
	}
}
