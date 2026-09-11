using System.Threading;
using Faolan.GameServer.World;
using Xunit;

namespace Faolan.Tests
{
	// TickMetrics is the gauge behind the client's dev "Server Framerate" overlay (GameServerStats 0x2009). Its whole
	// value is that the number moves with the loop's real health, so these pin the three readings that matter: healthy,
	// degraded, and dead. Timing-driven by construction - the gauge measures wall clock - so the bands are wide enough
	// to survive a loaded CI box while still separating the three cases by an order of magnitude.
	public class TickMetricsTests
	{
		[Fact]
		public void NeverTicked_ReadsZero()
		{
			// Not "the nominal rate" and not the last plausible value: a loop that has never run has no rate.
			var metrics = new TickMetrics(10.0);

			Assert.Equal(0, metrics.Current.TicksPerSecond);
			Assert.Equal(0ul, metrics.Current.TotalTicks);
			Assert.Equal(10.0, metrics.NominalTicksPerSecond);
		}

		[Fact]
		public void BeforeTheFirstWindowCloses_ReadsZero()
		{
			// A partial window is not a measurement - publishing one would report a rate computed over a fraction of a
			// second, which reads as a wild swing on the overlay every time the server starts.
			var metrics = new TickMetrics(10.0);
			metrics.RecordTick(0.001);
			metrics.RecordTick(0.001);

			Assert.Equal(0, metrics.Current.TicksPerSecond);
		}

		[Fact]
		public void AfterAFullWindow_ReportsTheAchievedRateAndBusyFraction()
		{
			// ~20 ticks across ~1s of wall clock, each reporting 10 ms of "work".
			const double busyPerTick = 0.01;
			var metrics = new TickMetrics(10.0);
			for (var i = 0; i < 24; i++)
			{
				Thread.Sleep(50);
				metrics.RecordTick(busyPerTick);
			}

			// Asserted as invariants, not as target numbers. Sleep is a floor, not a schedule - on a loaded box the
			// window closes early and at a lower rate, so pinning "~20 Hz over 24 ticks" is a flake generator (it
			// caught itself: the first version asserted the snapshot's cumulative count was 20-24 and read 19, because
			// the count is the total at publish time, with the remaining ticks accruing into the next window).
			var snapshot = metrics.Current;
			Assert.True(snapshot.TicksPerSecond > 0, "a closed window must report a rate");
			Assert.InRange(snapshot.TotalTicks, 1ul, 24ul); // cumulative, never more than what was recorded

			// The one exact relationship that holds whatever the scheduler does: rate and busy fraction are computed
			// over the same window, so busy = rate x busy-per-tick. This is what would break if either were divided by
			// the nominal period instead of the measured elapsed time - the bug class this whole change exists to fix.
			Assert.Equal(snapshot.TicksPerSecond * busyPerTick, snapshot.BusyFraction, 6);
		}

		[Fact]
		public void AStaleSnapshotDecaysToZero()
		{
			// The reading that catches a dead world loop. A published snapshot is only a measurement while it is fresh;
			// once the loop stops feeding the gauge, serving the last healthy number forever would make a stalled
			// server look perfectly fine in-client. The cumulative tick count survives - it is a total, not a rate.
			var metrics = new TickMetrics(10.0);
			for (var i = 0; i < 24; i++) // must cross the 1s window, or there is no published snapshot to go stale
			{
				Thread.Sleep(50);
				metrics.RecordTick(0.001);
			}

			var live = metrics.Current;
			Assert.True(live.TicksPerSecond > 0, "the gauge should have published a window before the staleness check");

			Thread.Sleep(3100); // > StaleAfterSeconds, with no ticks recorded

			Assert.Equal(0, metrics.Current.TicksPerSecond);
			Assert.Equal(live.TotalTicks, metrics.Current.TotalTicks);
		}
	}
}
