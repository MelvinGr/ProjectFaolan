namespace Faolan.GameServer.World
{
	/// <summary>
	///     Lets a behaviour run at its own cadence on top of a faster base game tick: the loop ticks at a high base
	///     rate so continuous systems such as movement and respawn stay smooth, while slower rate-based systems - such
	///     as vitals regen at 1 Hz - accumulate the base delta and fire when their interval has elapsed.
	/// </summary>
	/// <remarks>
	///     Fire-once semantics: when the interval is reached it reports the total elapsed time since the last fire and
	///     resets, so the behaviour can scale its effect by that (regen is rate times elapsed). It does not fire
	///     several times to catch up on a long stall, which avoids a spiral of death. A true fixed-step accumulator -
	///     carrying the remainder and firing N times - would be a separate type, for a discrete-step system that needs
	///     one.
	/// </remarks>
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

		/// <summary>
		///     Adds <paramref name="dt" /> seconds.
		/// </summary>
		/// <returns>
		///     True, with <paramref name="elapsed" /> set to the total accumulated time since the last fire (at least
		///     the interval), when the interval is reached - and the accumulator resets. Otherwise false, with
		///     <paramref name="elapsed" /> 0.
		/// </returns>
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
