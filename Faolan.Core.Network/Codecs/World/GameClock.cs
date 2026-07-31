using System;

namespace Faolan.Core.Network.Codecs.World
{
	// The world clock's TIME MODEL - the day/night cycle GameTimeIIR carries. Not a codec (nothing here encodes or
	// decodes): it is the policy that decides what value to send, which is why it sits beside GameTimeCodec rather
	// than inside it (#530 rule 1 - a codec exposes only Build and Parse).
	//
	// Both live fields are GENERATED rather than replayed (#351), each resting on a different source that the other
	// confirms - see docs/re/gametime-clock.md.
	public static class GameClock
	{
		// dayTime's UNIT, from the client's own clock class GameTime_t (gametime.cpp). Its ctor FUN_004ed470 sets
		// hoursPerDay=24 (+0x98), minutesPerHour=60 (+0x9C), secondsPerMinute=60 (+0xA0), secondsPerHour=3600 (+0xA4)
		// and a wrap period of 86400.0 (+0x78), and its readers decompose the value as
		//   seconds = t % 60,  minutes = (t / 60) % 60,  hours = t / 3600
		// (FUN_004ee4f0 SetCurrentRealTime / FUN_004ee710, the IIR handler). So dayTime counts GAME SECONDS with 86400
		// of them to a game day. Only minutes and seconds are taken modulo - hours is not - so the value is an absolute
		// running count and the time of day is `dayTime % 86400`.
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
		// confirmed by the corpus, which is what makes this model trustworthy: it is the period of the resync
		// timestamp's high word, derived from the 141 captured GameTimeIIR records two separate ways -
		//   * high == low / P holds exactly for 140/141 samples at P = 17280, and for 0/141 at 3600 / 21600 / 43200 /
		//     86400. The one miss is a field change, whose two halves straddle a period boundary (its low word is 155s
		//     past one), so it is not a clock tick.
		//   * solving for P directly as low/high over every sample lands in 17280.001 .. 17280.169.
		// The client says 86400 game seconds at 5x; the wire says 17280 real seconds. They agree exactly.
		public const uint GameDayRealSeconds = GameDaySeconds / GameSecondsPerRealSecond;

		// The resync anchor, as its two u32 fields: [gameDay][unixSeconds]. Both are wall-clock derived, which is what
		// makes this generatable rather than replayable:
		//   * `unixSeconds` is plain unix epoch seconds - it matches the capturing frame's own time for 141/141 records
		//     (0.2 .. 22.0s behind it, i.e. stamped at or just before the send). The client confirms the role: its
		//     handler stores `field - __time64(NULL)`, the skew against its own clock.
		//   * `gameDay` is that same value divided by GameDayRealSeconds - the game-day counter.
		public static (uint GameDay, uint UnixSeconds) ResyncAnchor(DateTimeOffset now)
		{
			var unixSeconds = (uint)now.ToUnixTimeSeconds();
			return (unixSeconds / GameDayRealSeconds, unixSeconds);
		}

		// Kept as a u64 view of the same two words, for the docs/tests that describe the anchor as one value.
		public static ulong ResyncTimestamp(DateTimeOffset now)
		{
			var (gameDay, unixSeconds) = ResyncAnchor(now);
			return ((ulong)gameDay << 32) | unixSeconds;
		}

		// The live time of day, in game seconds since game-midnight. Phase-locked to the wall clock the same way the
		// resync anchor is, so it agrees with the game-day counter in that anchor: the day rolls over to 0 exactly when
		// `unix / GameDayRealSeconds` ticks. `offsetGameSeconds` shifts the phase (the `.time` GM command).
		//
		// Retail's own dayTime is NOT phase-locked to unix time - our own corpus check rules that out - because retail's
		// is per-playfield with its own epoch (it even runs backwards between captures). The phase is therefore ours to
		// choose; this picks the one that is deterministic and survives a restart.
		public static float TimeOfDay(DateTimeOffset now, long offsetGameSeconds = 0)
		{
			var gameSeconds = now.ToUnixTimeSeconds() * GameSecondsPerRealSecond + offsetGameSeconds;
			return ((gameSeconds % GameDaySeconds) + GameDaySeconds) % GameDaySeconds;
		}
	}
}
