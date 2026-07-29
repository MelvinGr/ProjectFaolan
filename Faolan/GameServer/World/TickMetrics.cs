using System;
using System.Diagnostics;
using System.Threading;

namespace Faolan.GameServer.World
{
	// Measures how the simulation loop is ACTUALLY running: achieved ticks/sec and how much of each tick's budget the
	// work consumed. Feeds the client's dev overlay ("Server Framerate", GameServerStats 0x2009 - see
	// Session/KeepAliveService.cs), which used to show a hardcoded 89.6 that never moved no matter what the server did.
	//
	// The point of the readout is DEGRADATION, not the nominal number. Retail's own value behaved exactly that way:
	// across 4243 captured GameServerStats it sits pinned at its ceiling (median 98.9, max 99.34 -> a ~100 Hz loop) and
	// falls off a cliff under load, down to 0.13 during heavy playfield streaming. So a rate that reads its nominal
	// value means healthy; a rate well under it means the loop is not keeping up. Reporting the nominal rate as a
	// constant would throw away the only information the field carries.
	//
	// Threading: the tick loop is the SOLE writer and touches only its own window accumulators; readers (any client's
	// Ping handler, on a connection thread) read one published snapshot reference. Publication is a Volatile.Write of
	// an immutable record, so a reader either sees the whole previous window or the whole new one - never a torn mix
	// of a new tick count with an old elapsed time, which is what makes a rate computed from separate fields lie.
	public sealed class TickMetrics
	{
		// How much wall time each measurement window covers. Long enough that one slow tick doesn't dominate the
		// reading, short enough that the overlay reacts within a ping interval.
		private const double WindowSeconds = 1.0;

		// A snapshot older than this is not a measurement any more - the loop has stalled or died, so we report zero
		// rather than serving the last healthy number forever. That is what makes a dead world loop VISIBLE in-client
		// (0 FPS) instead of looking perfectly fine. #404's tick loop survives a bad tick, but not a bad timer.
		private const double StaleAfterSeconds = 3.0;

		private readonly double _nominalRate;

		private long _windowStart = Stopwatch.GetTimestamp();
		private int _windowTicks;
		private double _windowBusySeconds;
		private ulong _totalTicks;

		private Snapshot? _published;

		// nominalTicksPerSecond is the loop's configured cadence - carried for reference so a reader can say "10.0 of a
		// nominal 10.0" without knowing the loop's constants.
		public TickMetrics(double nominalTicksPerSecond)
		{
			_nominalRate = nominalTicksPerSecond;
		}

		public double NominalTicksPerSecond => _nominalRate;

		// The last completed measurement window, or a zeroed reading when the loop has not published one recently
		// (never started, or stalled - see StaleAfterSeconds).
		public Snapshot Current
		{
			get
			{
				var published = Volatile.Read(ref _published);
				if (published is null)
					return Snapshot.Idle;

				return Stopwatch.GetElapsedTime(published.Timestamp).TotalSeconds > StaleAfterSeconds
					? Snapshot.Idle with { TotalTicks = published.TotalTicks }
					: published;
			}
		}

		// Called by the simulation loop once per tick with the time that tick's WORK took (excluding the wait for the
		// next timer beat). Publishes a new snapshot when the window fills.
		public void RecordTick(double busySeconds)
		{
			_totalTicks++;
			_windowTicks++;
			if (busySeconds > 0)
				_windowBusySeconds += busySeconds;

			var elapsed = Stopwatch.GetElapsedTime(_windowStart).TotalSeconds;
			if (elapsed < WindowSeconds)
				return;

			Volatile.Write(ref _published, new Snapshot(_windowTicks / elapsed, Math.Clamp(_windowBusySeconds / elapsed, 0, 1), _totalTicks, Stopwatch.GetTimestamp()));

			_windowStart = Stopwatch.GetTimestamp();
			_windowTicks = 0;
			_windowBusySeconds = 0;
		}

		// One measurement window's reading. Immutable so it can be published by reference (see the threading note).
		// BusyFraction is the share of wall time the loop spent doing work: the loop's headroom, and the number that
		// says whether the base cadence could be raised.
		public sealed record Snapshot(double TicksPerSecond, double BusyFraction, ulong TotalTicks, long Timestamp)
		{
			public static readonly Snapshot Idle = new Snapshot(0, 0, 0, 0);
		}
	}
}
