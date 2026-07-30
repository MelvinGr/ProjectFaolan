using System.Collections.Generic;
using System.Linq;
using Faolan.Importer.Captures;
using Xunit;

namespace Faolan.Tests
{
	// The acceptance rule for a measured DoT tick interval (#437). These are the shapes the corpus actually produces,
	// asserted as a pure function so the judgement is pinned without needing the capture corpus: the estimator is the
	// one place that decides whether observed gaps establish a spell's cadence, and its failure mode is silent (a
	// wrong interval misprices every tick of that spell for the rest of the spell's life).
	public class DotIntervalEstimatorTests
	{
		// A clean train: the gaps scatter a little around the true period and snap to the round value retail used.
		// These are Incinerate's real quartiles from the corpus (2.13 / 2.15 / 2.15).
		[Fact]
		public void TightGapsSnapToTheRoundInterval()
		{
			Assert.Equal(2000u, DotIntervalEstimator.Estimate([2.13, 2.15, 2.15, 2.16, 2.14]));
		}

		// Sub-second DoTs exist (Aura: Fire Weave, Resting), so the quantum has to hold at the bottom of the range.
		[Fact]
		public void SnapsToTheHalfSecondQuantum()
		{
			Assert.Equal(500u, DotIntervalEstimator.Estimate([0.48, 0.51, 0.52, 0.49]));
			Assert.Equal(1500u, DotIntervalEstimator.Estimate([1.47, 1.54, 1.51, 1.63]));
		}

		// The asymmetry that motivates using the MEDIAN rather than the mean: a tick that was dropped, delayed or
		// simply not captured merges two periods into one long gap. It can never produce a SHORT one, so the tail is
		// one-sided and a handful of doubled gaps must not drag the estimate upwards.
		[Fact]
		public void AMissedTickDoesNotDragTheEstimateUp()
		{
			var gaps = new List<double> { 2.0, 2.1, 2.0, 2.05, 2.0, 2.1, 4.1, 6.2 };
			Assert.Equal(2000u, DotIntervalEstimator.Estimate(gaps));
		}

		// The case that a spread-based rule gets wrong, kept as its own gate because it is why the rule measures
		// CONCENTRATION instead. These are Fires of Gehenna's real corpus quartiles (p25 2.15 / median 2.20 /
		// p75 3.09): a quarter of its gaps are doubled periods, which blows out any quartile-ratio bound while the
		// spell is unmistakably a 2 s tick.
		[Fact]
		public void AOneSidedTailOfDoubledGapsIsStillAMeasurement()
		{
			var gaps = new List<double> { 2.15, 2.16, 2.18, 2.20, 2.20, 2.22, 2.25, 3.09, 4.30, 4.35 };
			Assert.Equal(2000u, DotIntervalEstimator.Estimate(gaps));
		}

		// Two gaps are a coincidence. A spell observed ticking three times in one capture is exactly the case that
		// looks like a measurement and is not one.
		[Fact]
		public void TooFewGapsIsNotAMeasurement()
		{
			Assert.Null(DotIntervalEstimator.Estimate([2.0, 2.0]));
			Assert.Null(DotIntervalEstimator.Estimate([2.0]));
			Assert.Null(DotIntervalEstimator.Estimate([]));
		}

		// Gaps spread across the whole range are not a periodic tick being observed - they are re-applications, a
		// second caster landing the same spell, or combat pausing. Rejected rather than averaged into a number.
		[Fact]
		public void RaggedGapsAreRejected()
		{
			// Ice Strike (Rank 6) as the corpus actually shows it: 2.15 / 4.20 / 8.80 quartiles over 38 gaps.
			var ragged = new List<double> { 2.1, 2.2, 2.4, 4.0, 4.2, 4.4, 8.5, 8.8, 9.1, 12.0 };
			Assert.Null(DotIntervalEstimator.Estimate(ragged));
		}

		// Out-of-range gaps are dropped BEFORE the count and spread tests, so a train padded with a long pause is
		// still measurable from the real ticks inside it - and a train that is only pauses is not.
		[Fact]
		public void OutOfRangeGapsAreDiscardedNotClamped()
		{
			Assert.Equal(3000u, DotIntervalEstimator.Estimate([3.0, 3.05, 2.95, 3.0, 900.0, 0.01]));
			Assert.Null(DotIntervalEstimator.Estimate([900.0, 1200.0, 0.01, 0.02]));
		}

		// The estimator must be blind to the order gaps arrive in - they are pooled across captures, and a caller
		// should never have to sort them first.
		[Fact]
		public void OrderDoesNotMatter()
		{
			var gaps = new List<double> { 5.11, 4.92, 4.95, 5.0, 4.98 };
			Assert.Equal(DotIntervalEstimator.Estimate(gaps), DotIntervalEstimator.Estimate(gaps.AsEnumerable().Reverse()));
			Assert.Equal(5000u, DotIntervalEstimator.Estimate(gaps));
		}
	}
}
