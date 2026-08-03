using System.Diagnostics;

namespace Faolan.Core
{
	// The server's UPTIME clock - the one time base the client shares with us, and the only one an absolute wire
	// timestamp may be expressed on.
	//
	// The client keeps it in `ServerTime_c` (a global singleton, `FUN_007a0d60`) and has exactly ONE way to learn it:
	// the `DispatchPong` (GameCharInterface 0x2082) reply to its LatencyCheck poll, whose last two words are a 64-bit
	// MICROSECOND counter. The client's slot for that signal (`FUN_007a0ea0`) divides it by 1000 and stores the result,
	// so what it holds is the server's uptime in MILLISECONDS. It never advances the value itself between pongs - a
	// consumer compares against whatever the last reply carried.
	// [V1: tools/ghidra/DumpServerTimeClock.java - the ServerTime_c ctor, its signal slot, and the emitter thunk]
	//
	// Two senders are on this clock and MUST stay on the same origin, which is the whole reason this lives here rather
	// than as a private static in either of them:
	//   - `KeepAliveService.HandleLatencyCheck` - the pong itself, in microseconds.
	//   - the `MagicBuffIIR` apply payload's @46 u64 - an ABSOLUTE EXPIRY in milliseconds, which the client turns back
	//     into a remaining time as `(stamp - ServerTime) / 10` centiseconds.
	// Split them onto two origins and every buff either resumes at the wrong time or is silently discarded, with
	// nothing on the wire looking wrong - `MagicBuffExpiryStampTests` is the gate that keeps them together.
	//
	// ⚠️ NOT `GameClock` (Faolan.Core.Network/Codecs/World). That one is the world's DAY/NIGHT cycle - game seconds at
	// 5x real time, anchored to unix wall-clock, carried by GameTimeIIR. This one is process uptime in a flat SI unit
	// and is never rendered. They share no field and no origin; the only thing they have in common is the word clock.
	//
	// A Stopwatch, not Environment.TickCount64: the counter has to be monotonic and rooted at OUR process start, not
	// at the machine's boot (retail's is a real server uptime - 1 to 5 days across the capture corpus).
	public static class ServerUptimeClock
	{
		private static readonly long Started = Stopwatch.GetTimestamp();

		// Uptime in MICROSECONDS - the unit DispatchPong carries.
		public static ulong Microseconds => (ulong)Stopwatch.GetElapsedTime(Started).TotalMicroseconds;

		// Uptime in MILLISECONDS - the unit the client stores, and the unit an absolute wire expiry is expressed in.
		// Deliberately derived from Microseconds by the SAME division the client does, so the two can never drift apart
		// by a rounding rule.
		public static ulong Milliseconds => Microseconds / 1000;

		// The absolute expiry an effect lasting `durationMilliseconds` from now carries on the wire.
		public static ulong ExpiryFromNow(ulong durationMilliseconds)
		{
			return Milliseconds + durationMilliseconds;
		}
	}
}
