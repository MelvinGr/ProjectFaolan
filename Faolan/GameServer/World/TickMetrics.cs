using System;
using System.Diagnostics;
using System.Threading;

namespace Faolan.GameServer.World
{
	/// <summary>
	///     Measures how the simulation loop is actually running: achieved ticks per second, and how much of each tick's
	///     budget the work consumed. Feeds the client's dev overlay ("Server Framerate", GameServerStats 0x2009).
	/// </summary>
	/// <remarks>
	///     <para>
	///         The point of the readout is degradation rather than the nominal number. Retail's own value behaves that
	///         way: across 4243 captured GameServerStats it sits pinned at its ceiling (median 98.9, max 99.34, so a
	///         roughly 100 Hz loop) and falls off a cliff under load, down to 0.13 during heavy playfield streaming. A
	///         rate reading its nominal value means healthy; a rate well under it means the loop is not keeping up, so
	///         reporting the nominal rate as a constant throws away the only information the field carries.
	///     </para>
	///     <para>
	///         Threading: the tick loop is the sole writer and touches only its own window accumulators, while readers
	///         - any client's Ping handler, on a connection thread - read one published snapshot reference.
	///         Publication is a <c>Volatile.Write</c> of an immutable record, so a reader sees either the whole
	///         previous window or the whole new one, never a new tick count torn against an old elapsed time.
	///     </para>
	/// </remarks>
	public sealed class TickMetrics
	{
		// How much wall time each measurement window covers: long enough that one slow tick does not dominate the
		// reading, short enough that the overlay reacts within a ping interval.
		private const double WindowSeconds = 1.0;

		// A snapshot older than this is not a measurement any more - the loop has stalled or died - so zero is reported
		// rather than the last healthy number for ever. That is what makes a dead world loop visible in-client, as
		// 0 FPS, instead of looking perfectly fine: the tick loop survives a bad tick, but not a bad timer.
		private const double StaleAfterSeconds = 3.0;

		private readonly double _nominalRate;

		private long _windowStart = Stopwatch.GetTimestamp();
		private int _windowTicks;
		private double _windowBusySeconds;
		private ulong _totalTicks;

		private Snapshot? _published;

		/// <param name="nominalTicksPerSecond">
		///     The loop's configured cadence, carried for reference so a reader can say "10.0 of a nominal 10.0"
		///     without knowing the loop's constants.
		/// </param>
		public TickMetrics(double nominalTicksPerSecond)
		{
			_nominalRate = nominalTicksPerSecond;
		}

		public double NominalTicksPerSecond => _nominalRate;

		/// <summary>
		///     The last completed measurement window, or a zeroed reading when the loop has not published one recently:
		///     never started, or stalled.
		/// </summary>
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

		/// <summary>
		///     Called by the simulation loop once per tick with the time that tick's work took, excluding the wait for
		///     the next timer beat. Publishes a new snapshot when the window fills.
		/// </summary>
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

		/// <summary>
		///     One measurement window's reading. Immutable so it can be published by reference - see the threading note
		///     on the class.
		/// </summary>
		/// <param name="BusyFraction">
		///     The share of wall time the loop spent doing work: its headroom, and the number that says whether the
		///     base cadence could be raised.
		/// </param>
		public sealed record Snapshot(double TicksPerSecond, double BusyFraction, ulong TotalTicks, long Timestamp)
		{
			public static readonly Snapshot Idle = new Snapshot(0, 0, 0, 0);
		}
	}
}
