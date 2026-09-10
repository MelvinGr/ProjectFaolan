using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Faolan.GameServer.Session
{
	// Where a world entry's time goes, step by step. `SendBurstlessWorldEntry` marks each step as it finishes and
	// logs the whole line at the end, so every real entry in faolan.log carries its own profile - which is how the
	// "first player in the map" spike can be attributed rather than guessed at (#938).
	//
	// ⭐ The reason it is a breakdown and not a total: the total mixes three costs with three different fixes. What
	// is paid once per SERVER lifetime (a lazily built catalog) is a startup warm-up; what is paid once per MAP (the
	// zone's NPCs, objects and terrain) is a map pre-load; what is paid on EVERY entry (the character's own rows,
	// the snapshot) is a query to make cheaper. One number cannot say which you are looking at.
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
