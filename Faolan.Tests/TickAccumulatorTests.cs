using Faolan.GameServer.World;
using Xunit;

namespace Faolan.Tests
{
	// The per-behaviour tick accumulator (docs/server-architecture-roadmap.md): lets a behaviour run at its own
	// cadence on a faster base tick, reporting the actual elapsed time so it can dt-scale.
	public class TickAccumulatorTests
	{
		[Fact]
		public void Advance_FiresOncePerCall_EvenOnLongStall()
		{
			// A single large dt fires once (no catch-up storm) and reports the whole elapsed for dt-scaling.
			var acc = new TickAccumulator(1.0);
			Assert.True(acc.Advance(10.0, out var e));
			Assert.Equal(10.0, e, 6);
			Assert.False(acc.Advance(0.1, out _)); // fully consumed; nothing carried over
		}

		[Fact]
		public void Advance_FiresOnlyWhenIntervalReached()
		{
			var acc = new TickAccumulator(1.0);

			Assert.False(acc.Advance(0.4, out var e1));
			Assert.Equal(0, e1);
			Assert.False(acc.Advance(0.4, out _)); // 0.8 total, not yet
			Assert.True(acc.Advance(0.4, out var e3)); // 1.2 total -> fires
			Assert.Equal(1.2, e3, 6); // reports the full accumulated elapsed (for dt-scaling)
		}

		[Fact]
		public void Advance_IgnoresNonPositiveDt()
		{
			var acc = new TickAccumulator(1.0);
			Assert.False(acc.Advance(0, out _));
			Assert.False(acc.Advance(-5, out _));
			Assert.True(acc.Advance(1.0, out _));
		}

		[Fact]
		public void Advance_ResetsAfterFiring()
		{
			var acc = new TickAccumulator(1.0);

			Assert.True(acc.Advance(1.0, out _));
			Assert.False(acc.Advance(0.5, out _)); // accumulator reset; 0.5 < 1.0
			Assert.True(acc.Advance(0.5, out var e)); // 1.0 again
			Assert.Equal(1.0, e, 6);
		}
	}
}
