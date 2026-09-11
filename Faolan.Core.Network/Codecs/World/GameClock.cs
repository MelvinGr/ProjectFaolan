using System;

namespace Faolan.Core.Network.Codecs.World
{
	/// <summary>
	///     The world clock's time model - the day/night cycle GameTimeIIR carries. Not a codec, since nothing here encodes
	///     or decodes: it is the policy that decides what value to send, which is why it sits beside
	///     <see cref="GameTimeCodec" /> rather than inside it.
	/// </summary>
	/// <remarks>
	///     Both live fields are generated rather than replayed, each resting on a different source that the other confirms;
	///     see docs/re/gametime-clock.md.
	/// </remarks>
	public static class GameClock
	{
		/// <summary>
		///     dayTime's unit, from the client's own clock class GameTime_t (gametime.cpp): it counts game seconds, with
		///     86400 of them to a game day.
		/// </summary>
		/// <remarks>
		///     The constructor FUN_004ed470 sets hoursPerDay 24 (+0x98), minutesPerHour 60 (+0x9C), secondsPerMinute 60
		///     (+0xA0), secondsPerHour 3600 (+0xA4) and a wrap period of 86400.0 (+0x78), and its readers decompose the value
		///     as <c>seconds = t % 60</c>, <c>minutes = (t / 60) % 60</c>, <c>hours = t / 3600</c> (FUN_004ee4f0
		///     SetCurrentRealTime and FUN_004ee710, the IIR handler). Only minutes and seconds are taken modulo, not hours,
		///     so the value is an absolute running count and the time of day is <c>dayTime % 86400</c>.
		/// </remarks>
		public const uint GameDaySeconds = 86400;

		/// <summary>
		///     How many game seconds pass per real second, from the same class: the per-frame tick FUN_004eddb0 advances the
		///     clock by <c>this[0x74] * dt * rate[0xBC] * wireRate[0xD4] * phaseRate</c>, and the constructor sets +0x74 to 5.
		///     The rates all default to 1.0f, and the wire <c>rate</c> field feeds +0xD4 directly.
		/// </summary>
		/// <remarks>
		///     Two things that tick also shows, not modelled here: +0xC0 and +0xC4 are separate day and night rate
		///     multipliers, so the two halves of the day can run at different speeds, selected by a dawn/dusk window built
		///     from +0xA8 = 19h, +0xAC = 4h, +0xB0 = 7h and +0xB4 = 2h; and +0x80 and +0x88 hold 21600.0 (06:00) and 64800.0
		///     (18:00), the day window itself.
		/// </remarks>
		public const uint GameSecondsPerRealSecond = 5;

		/// <summary>
		///     One game day therefore takes 17280 real seconds, or 4.8 real hours.
		/// </summary>
		/// <remarks>
		///     The corpus confirms the number independently, which is what makes the model trustworthy: it is the period of
		///     the resync timestamp's high word, derived from the 141 captured GameTimeIIR records two ways. The relation
		///     <c>high == low / P</c> holds exactly for 140 of 141 samples at P = 17280 and for none at 3600, 21600, 43200 or
		///     86400 - the one miss being a field change whose two halves straddle a period boundary rather than a clock tick
		///     - and solving for P directly as low over high across every sample lands between 17280.001 and 17280.169. The
		///     client says 86400 game seconds at five times speed; the wire says 17280 real seconds.
		/// </remarks>
		public const uint GameDayRealSeconds = GameDaySeconds / GameSecondsPerRealSecond;

		/// <summary>
		///     The resync anchor, as its two u32 fields: the game day and unix seconds. Both are wall-clock derived, which is
		///     what makes the record generatable rather than replayable.
		/// </summary>
		/// <remarks>
		///     <c>unixSeconds</c> is plain unix epoch seconds: it matches the capturing frame's own time on all 141 records,
		///     0.2 to 22.0 seconds behind it, so it is stamped at or just before the send, and the client's handler stores
		///     <c>field - __time64(NULL)</c>, the skew against its own clock. <c>gameDay</c> is that same value divided by
		///     <see cref="GameDayRealSeconds" />.
		/// </remarks>
		public static (uint GameDay, uint UnixSeconds) ResyncAnchor(DateTimeOffset now)
		{
			var unixSeconds = (uint)now.ToUnixTimeSeconds();
			return (unixSeconds / GameDayRealSeconds, unixSeconds);
		}

		/// <summary>A u64 view of the same two words, for the docs and tests that describe the anchor as one value.</summary>
		public static ulong ResyncTimestamp(DateTimeOffset now)
		{
			var (gameDay, unixSeconds) = ResyncAnchor(now);
			return ((ulong)gameDay << 32) | unixSeconds;
		}

		/// <summary>
		///     The live time of day, in game seconds since game-midnight. It is phase-locked to the wall clock the same way
		///     the resync anchor is, so it agrees with that anchor's game-day counter: the day rolls over to 0 exactly when
		///     <c>unix / GameDayRealSeconds</c> ticks. <paramref name="offsetGameSeconds" /> shifts the phase, which is what
		///     the <c>.time</c> GM command does.
		/// </summary>
		/// <remarks>
		///     Retail's own dayTime is not phase-locked to unix time - the corpus rules that out - because retail's is
		///     per-playfield with its own epoch, and even runs backwards between captures. The phase is therefore ours to
		///     choose, and this picks the one that is deterministic and survives a restart.
		/// </remarks>
		public static float TimeOfDay(DateTimeOffset now, long offsetGameSeconds = 0)
		{
			var gameSeconds = now.ToUnixTimeSeconds() * GameSecondsPerRealSecond + offsetGameSeconds;
			return ((gameSeconds % GameDaySeconds) + GameDaySeconds) % GameDaySeconds;
		}
	}
}
