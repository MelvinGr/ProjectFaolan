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
		//
		// ⚠️ This shape is exactly what the second rule (#657) must NOT accept, and it would without its
		// contains-the-shortest-gap condition: {4.0, 4.2, 4.4} is a tight cluster of three, and 4000 ms is wrong.
		[Fact]
		public void RaggedGapsAreRejected()
		{
			var ragged = new List<double> { 2.1, 2.2, 2.4, 4.0, 4.2, 4.4, 8.5, 8.8, 9.1, 12.0 };
			Assert.Null(DotIntervalEstimator.Estimate(ragged));
		}

		// #657: the median is the wrong pivot once MORE than half the gaps are stretched, because it lands in the
		// tail itself. Ice Strike (Rank 6) as the corpus actually shows it - 38 gaps whose median is 4.20 s while
		// 13 of them sit inside 3.5% of each other at ~2.15 s, which is what every rank sibling measures at.
		//
		// The first rule rejects it (6 gaps near the median, 19 needed). The second reads the cluster at the bottom.
		[Fact]
		public void ATightClusterAtTheBottomIsAMeasurementEvenWhenTheMedianIsInTheTail()
		{
			var iceStrike = new List<double>
			{
				2.07, 2.11, 2.13, 2.13, 2.15, 2.15, 2.15, 2.15, 2.15, 2.15, 2.21, 2.21, 2.22,
				2.46, 2.76, 2.76, 3.03, 3.38, 3.52,
				4.88, 4.88, 4.92, 4.92, 5.45, 5.53, 6.11, 6.92, 6.92, 8.80, 8.91, 9.41, 9.52,
				10.98, 11.09, 11.73, 11.73, 19.82, 22.32
			};
			Assert.Equal(2000u, DotIntervalEstimator.Estimate(iceStrike));
		}

		// The condition that makes the second rule safe, on its own: the same cluster, with a handful of SHORTER
		// gaps under it, is a harmonic rather than the period - nothing can shorten a gap below the true interval,
		// so a gap below the cluster says the cluster is not the bottom of the distribution.
		[Fact]
		public void ATightClusterWithShorterGapsBelowItIsNotTheInterval()
		{
			var harmonic = new List<double> { 1.02, 1.05, 2.05, 2.10, 2.10, 2.12, 5.9, 6.4, 7.1, 9.8, 12.4, 14.0 };
			Assert.Null(DotIntervalEstimator.Estimate(harmonic));
		}

		// Where both rules speak they normally CORROBORATE - which is what makes letting the cluster lead safe. Fires
		// of Gehenna's real shape, with its doubled-period tail: the bottom cluster and the median both say 2000, and
		// the tail cluster at 4.3 gets no vote from either.
		[Fact]
		public void TheTwoRulesCorroborateOnAWellObservedSpell()
		{
			var gaps = new List<double> { 2.15, 2.16, 2.18, 2.20, 2.20, 2.22, 2.25, 4.30, 4.31, 4.35 };
			Assert.Equal(2000u, DotIntervalEstimator.Estimate(gaps));
		}

		// #660: and where they DISAGREE, the tight cluster is the one to believe. Ice Strike (Rank 1) as the corpus
		// shows it - 42 of 93 gaps inside 3% of each other at ~2.15 s, the same cluster its rank-6 sibling has, but a
		// median of 2.365 s. The median is only 10% above the cluster, yet it falls the wrong side of the 2.25 s
		// midpoint, so the half-second snap converts that 10% into a reported 2500 ms.
		//
		// This is the one spell in the corpus where the two rules differ, and it is why the cluster LEADS rather than
		// merely filling in after a rejection.
		[Fact]
		public void ATightClusterBeatsAMedianDraggedUpByAStretchedTail()
		{
			var gaps = new List<double>();
			gaps.AddRange(Enumerable.Repeat(2.15, 40));
			gaps.AddRange([2.12, 2.16]);
			gaps.AddRange([2.6, 2.9, 3.2, 3.4, 4.3, 4.4, 4.5, 6.4, 6.5, 8.6, 8.7, 10.8]);

			Assert.Equal(2000u, DotIntervalEstimator.Estimate(gaps));
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
