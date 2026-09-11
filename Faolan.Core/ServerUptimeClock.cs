using System.Diagnostics;

namespace Faolan.Core
{
	/// <summary>
	///     The server's uptime clock - the one time base the client shares with us, and the only one an absolute wire
	///     timestamp may be expressed on.
	/// </summary>
	/// <remarks>
	///     The client keeps it in <c>ServerTime_c</c> (a global singleton, FUN_007a0d60) and has exactly one way to
	///     learn it: the <c>DispatchPong</c> (GameCharInterface 0x2082) reply to its LatencyCheck poll, whose last two
	///     words are a 64-bit microsecond counter. The client's slot for that signal (FUN_007a0ea0) divides it by 1000
	///     and stores the result, so what it holds is the server's uptime in milliseconds. It never advances the value
	///     itself between pongs - a consumer compares against whatever the last reply carried.
	///     [V1: tools/ghidra/DumpServerTimeClock.java - the ServerTime_c ctor, its signal slot, and the emitter thunk]
	///     <para />
	///     Two senders are on this clock and must stay on the same origin, which is why it lives here rather than as a
	///     private static in either of them:
	///     <list type="bullet">
	///         <item><c>KeepAliveService.HandleLatencyCheck</c> - the pong itself, in microseconds.</item>
	///         <item>
	///             the <c>MagicBuffIIR</c> apply payload's @46 u64 - an absolute expiry in milliseconds, which the
	///             client turns back into a remaining time as <c>(stamp - ServerTime) / 10</c> centiseconds.
	///         </item>
	///     </list>
	///     Split them onto two origins and every buff either resumes at the wrong time or is silently discarded, with
	///     nothing on the wire looking wrong; <c>MagicBuffExpiryStampTests</c> is the gate that keeps them together.
	///     <para />
	///     This is not <c>GameClock</c> (Faolan.Core.Network/Codecs/World). That one is the world's day/night cycle -
	///     game seconds at 5x real time, anchored to unix wall-clock, carried by GameTimeIIR. This one is process
	///     uptime in a flat SI unit and is never rendered. They share no field and no origin.
	///     <para />
	///     A <see cref="Stopwatch" />, not <c>Environment.TickCount64</c>: the counter has to be monotonic and rooted
	///     at this process's start, not at the machine's boot (retail's is a real server uptime - 1 to 5 days across
	///     the capture corpus).
	/// </remarks>
	public static class ServerUptimeClock
	{
		private static readonly long Started = Stopwatch.GetTimestamp();

		/// <summary>Uptime in microseconds - the unit <c>DispatchPong</c> carries.</summary>
		public static ulong Microseconds => (ulong)Stopwatch.GetElapsedTime(Started).TotalMicroseconds;

		/// <summary>
		///     Uptime in milliseconds - the unit the client stores, and the unit an absolute wire expiry is expressed
		///     in. Deliberately derived from <see cref="Microseconds" /> by the same division the client does, so the
		///     two can never drift apart by a rounding rule.
		/// </summary>
		public static ulong Milliseconds => Microseconds / 1000;

		/// <summary>
		///     The absolute expiry an effect lasting <paramref name="durationMilliseconds" /> from now carries on the
		///     wire.
		/// </summary>
		public static ulong ExpiryFromNow(ulong durationMilliseconds)
		{
			return Milliseconds + durationMilliseconds;
		}
	}
}
