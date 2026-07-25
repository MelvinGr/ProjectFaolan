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

		// dayTime's UNIT, from the client's own clock class GameTime_t (gametime.cpp). Its ctor FUN_004ed470 sets
		// hoursPerDay=24 (+0x98), minutesPerHour=60 (+0x9C), secondsPerMinute=60 (+0xA0), secondsPerHour=3600 (+0xA4)
		// and a wrap period of 86400.0 (+0x78), and its readers decompose the value as
		//   seconds = t % 60,  minutes = (t / 60) % 60,  hours = t / 3600
		// (FUN_004ee4f0 SetCurrentRealTime / FUN_004ee710, the IIR handler). So dayTime is a count of GAME SECONDS with
		// 86400 of them to a game day. Only minutes and seconds are taken modulo - hours is not - so the value is an
		// absolute running count and the time of day is `dayTime % 86400`.
		public const uint GameDaySeconds = 86400;

		// How many game seconds pass per REAL second, from the same class: the per-frame tick FUN_004eddb0 advances the
		// clock by `this[0x74] * dt * rate[0xBC] * wireRate[0xD4] * phaseRate`, and the ctor sets +0x74 = 5 (the rates
		// all default to 1.0f, and the wire `rate` field feeds +0xD4 directly).
		//
		// Two side findings from that tick, not modelled here: +0xC0 / +0xC4 are SEPARATE day and night rate
		// multipliers (so the two halves of the day can run at different speeds - both default to 1.0), selected by a
		// dawn/dusk window built from +0xA8=19h, +0xAC=4h, +0xB0=7h, +0xB4=2h; and +0x80 / +0x88 hold 21600.0 (06:00)
		// and 64800.0 (18:00), the day window itself.
		public const uint GameSecondsPerRealSecond = 5;

		// ...so one game day takes 86400 / 5 = 17280 real seconds = 4.8 real hours. That number is independently
		// confirmed by the corpus, which is what makes this whole model trustworthy: it is the period of the resync
		// timestamp's high word, derived from the 141 captured GameTimeIIR records two separate ways -
		//   * high == low / P holds exactly for 140/141 samples at P = 17280, and for 0/141 at 3600 / 21600 / 43200 /
		//     86400. The one miss is a field change, whose two halves straddle a period boundary (its low word is 155s
		//     past one), so it is not a clock tick.
		//   * solving for P directly as low/high over every sample lands in 17280.001 .. 17280.169.
		// The client says 86400 game seconds at 5x; the wire says 17280 real seconds. They agree exactly.
		public const uint GameDayRealSeconds = GameDaySeconds / GameSecondsPerRealSecond;

		private const byte ClockFlag = 0x01;
		private const float ClockRate = 1.0f;

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

		// The live time of day, in game seconds since game-midnight. Phase-locked to the wall clock the same way the
		// resync anchor is, so it agrees with the game-day counter in that anchor: the day rolls over to 0 exactly when
		// `unix / GameDayRealSeconds` ticks. `offsetGameSeconds` shifts the phase (the `.time` GM command).
		//
		// Retail's own dayTime is NOT phase-locked to unix time - our own corpus check rules that out
		// ((unix % 17280) * 5 does not reproduce the captured values) - because retail's is per-playfield with its own
		// epoch (it even runs backwards between captures). The phase is therefore ours to choose; this picks the one
		// that is deterministic and survives a restart.
		public static uint TimeOfDay(DateTimeOffset now, long offsetGameSeconds = 0)
		{
			var gameSeconds = now.ToUnixTimeSeconds() * GameSecondsPerRealSecond + offsetGameSeconds;
			return (uint)(((gameSeconds % GameDaySeconds) + GameDaySeconds) % GameDaySeconds);
		}

		// dayTime on the wire is 32.32 fixed point - the integer game-second count in the high word, a fraction in the
		// low word (19 of the 141 captured records carry 0x80000000, i.e. half a second). We emit whole seconds; the
		// client interpolates between resyncs off its own frame delta anyway.
		public static ulong DayTime(DateTimeOffset now, long offsetGameSeconds = 0)
		{
			return (ulong)TimeOfDay(now, offsetGameSeconds) << 32;
		}

		// dayTime is passed in rather than read from the clock so a golden test can rebuild a captured record exactly:
		// the time of day is server state (like a position), while the record's LAYOUT is what the capture pins.
		public static byte[] Build(ulong dayTime, DateTimeOffset now)
		{
			return new FaolanStream().WriteUInt32(Opcode).WriteArray(CreateEditorRecordBuilder.BuildGameTimeIir(dayTime, ClockFlag, ResyncTimestamp(now), ClockRate)).ToArray();
		}

		public static byte[] Build(DateTimeOffset now, long offsetGameSeconds = 0)
		{
			return Build(DayTime(now, offsetGameSeconds), now);
		}
	}
}
