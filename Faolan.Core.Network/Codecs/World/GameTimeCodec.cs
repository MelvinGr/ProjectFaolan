using System;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Spawn;

namespace Faolan.Core.Network.Codecs.World
{
	// GameTimeIIR ("SetDayTime") - the world clock that drives the day/night cycle. Record =
	// [subop][TypeTag=0][Handle=0][u64 dayTime][u8 flag][u64 timestamp][f32 rate]. The body is the same 29-byte record
	// CreateEditorRecordBuilder.BuildGameTimeIir builds, so we share that builder (#351 side-cleanup) and just prepend
	// the subop here.
	//
	// `timestamp` is the resync anchor and is a PURE FUNCTION OF WALL-CLOCK TIME (recovered from the corpus - see
	// ResyncTimestamp), so it is computed live rather than replayed. `dayTime` is the clock the sun/lighting reads and
	// its unit is still unpinned, so it stays the captured constant - see the comment on CapturedDayTime.
	public static class GameTimeCodec
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.GameTimeIIR;

		// The real-time length of one game day, in seconds - the period of the resync timestamp's high word. Derived
		// twice over from the 141 captured GameTimeIIR records, the two derivations independent of each other:
		//   * high == low / P holds exactly for 140/141 samples at P = 17280, and for 0/141 at 3600 / 21600 / 43200 /
		//     86400. The one miss is a field change, whose two halves straddle a period boundary (its low word is 155s
		//     past one), so it is not a clock tick.
		//   * solving for P directly as low/high over every sample lands in 17280.001 .. 17280.169.
		// 17280s = 4.8 real hours per game day.
		public const uint GameDayRealSeconds = 17280;

		private const byte ClockFlag = 0x01;
		private const float ClockRate = 1.0f;

		// The captured world-entry dayTime. Still a constant because its UNIT is the one part of the clock the corpus
		// cannot settle (#351): dayTime is NOT a global monotonic clock - across captures it moves backwards, and even
		// within one continuous capture it goes up then down (Hakotep_hox_login_gameplay0: 21459957 -> 21458914), so no
		// rate can be fitted from it. That fits it being per-playfield (Tortage has separate day and night instances,
		// #149), which would also make the "global, no map data" reading of this record wrong. Ghidra does not settle it
		// either: every use of 86400 in the client is real wall-clock (the CRT gmtime helpers, a d/h/m/s countdown
		// formatter, and a localtime64-based hour-of-day schedule gate), and neither the IIR hash nor its type name
		// appears as a literal, so the reader is not statically reachable. Pinning it needs the in-client experiment.
		private const ulong CapturedDayTime = 0x01472BDD_00000000;

		// The resync anchor: [u32 game-day number][u32 unix epoch seconds]. Both halves are wall-clock derived, which is
		// what makes this generatable rather than replayable:
		//   * the LOW word is plain unix epoch seconds - it matches the capturing frame's own time for 141/141 records
		//     (0.2 .. 22.0s behind it, i.e. stamped at or just before the send).
		//   * the HIGH word is that same value divided by GameDayRealSeconds - the game-day counter.
		public static ulong ResyncTimestamp(DateTimeOffset now)
		{
			var unixSeconds = (ulong)now.ToUnixTimeSeconds();
			return ((unixSeconds / GameDayRealSeconds) << 32) | (uint)unixSeconds;
		}

		public static byte[] Build(DateTimeOffset now)
		{
			return new FaolanStream().WriteUInt32(Opcode).WriteArray(CreateEditorRecordBuilder.BuildGameTimeIir(CapturedDayTime, ClockFlag, ResyncTimestamp(now), ClockRate)).ToArray();
		}

		public static byte[] Build()
		{
			return Build(DateTimeOffset.UtcNow);
		}
	}
}
