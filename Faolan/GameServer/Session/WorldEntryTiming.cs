using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Faolan.GameServer.Session
{
	// Where a world entry's time goes, step by step. `SendBurstlessWorldEntry` marks each step as it finishes and logs
	// the whole line at the end, so every real entry in faolan.log carries its own profile.
	//
	// It is a breakdown rather than a total because the total mixes three costs with three different fixes: what is
	// paid once per server lifetime (a lazily built catalog) is a startup warm-up, what is paid once per map (the
	// zone's NPCs, objects and terrain) is a map pre-load, and what is paid on every entry (the character's own rows,
	// the snapshot) is a query to make cheaper. One number cannot say which of the three is in front of you.
	public sealed class WorldEntryTiming
	{
		private readonly Stopwatch _clock = Stopwatch.StartNew();
		private readonly List<(string Step, long Ms)> _steps = [];
		private long _last;

		// Closes the step that has been running since the previous mark (or since construction) and names it.
		public void Mark(string step)
		{
			var now = _clock.ElapsedMilliseconds;
			_steps.Add((step, now - _last));
			_last = now;
		}

		public IReadOnlyList<(string Step, long Ms)> Steps => _steps;
		public long TotalMs => _clock.ElapsedMilliseconds;

		// One line, slowest steps first, so the culprit is the first thing the eye lands on.
		public override string ToString()
		{
			return $"total {TotalMs} ms: " + string.Join(", ", _steps.OrderByDescending(s => s.Ms).Select(s => $"{s.Step} {s.Ms}"));
		}
	}
}
