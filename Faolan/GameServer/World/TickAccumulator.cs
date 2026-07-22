namespace Faolan.GameServer.World
{
	// Lets a behaviour run at its own cadence on top of a faster base game tick (Gitea #404).
	// The loop ticks at a high base rate (so continuous systems - movement/respawn - stay smooth); slower rate-based
	// systems (e.g. vitals regen at 1 Hz) accumulate the base dt and fire when their interval has elapsed.
	//
	// Fire-once semantics (for rate-based behaviours): when the interval is reached it reports the TOTAL elapsed time
	// since the last fire and resets, so the behaviour can dt-scale its effect (e.g. regen ~= rate * elapsed). It does
	// NOT fire multiple times to "catch up" on a long stall, which avoids a spiral of death; a true fixed-step
	// (carry-remainder, fire-N-times) accumulator can be added if a discrete-step system needs it.
	public sealed class TickAccumulator
	{
		private readonly double _interval;
		private double _accumulated;

		public TickAccumulator(double intervalSeconds)
		{
			// A non-positive interval would fire every tick; clamp to a tiny positive value so it stays well-defined.
			_interval = intervalSeconds > 0
				? intervalSeconds
				: double.Epsilon;
		}

		// Adds `dt` seconds. When the accumulated time reaches the interval, returns true with `elapsed` = the total
		// accumulated time since the last fire (>= interval) and resets; otherwise returns false with elapsed = 0.
		public bool Advance(double dt, out double elapsed)
		{
			if (dt > 0)
				_accumulated += dt;

			if (_accumulated < _interval)
			{
				elapsed = 0;
				return false;
			}

			elapsed = _accumulated;
			_accumulated = 0;
			return true;
		}
	}
}
