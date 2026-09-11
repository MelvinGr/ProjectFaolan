using System;
using System.Collections.Generic;
using System.Linq;

namespace Faolan.Importer.Captures
{
	/// <summary>
	///     Turns the observed gaps between consecutive DoT ticks of one spell into that spell's tick interval, or into
	///     "not measurable". Pure and separate from the capture walk so the acceptance rule is testable without a
	///     corpus.
	/// </summary>
	/// <remarks>
	///     A capture is a lossy record of a periodic event, and the two distortions that matter are not symmetric:
	///     a dropped, delayed or unobserved tick can only ever lengthen a gap (two 2 s ticks with the middle one
	///     missing read as one 4 s gap) - nothing can shorten one below the true interval; and a re-application (the
	///     caster recasts the DoT, or a second caster lands the same spell on the same target) starts a fresh tick
	///     train, so its first gap is an arbitrary fraction of the interval.
	///     So the raw gaps of a real DoT cluster tightly at the interval with a long upper tail, and the estimator's job
	///     is to accept that shape and reject everything else - a spell seen ticking twice says nothing, and one whose
	///     gaps are spread across the whole range is not being observed as a periodic tick at all.
	///     Two acceptance rules are tried in order: the tight bottom-cluster reading (see
	///     <see cref="TightClusterTolerance" />) leads wherever it has an answer, and the concentration test around the
	///     median answers where it declines.
	///     Known limit - the harmonic case: a spell whose ticks are mostly unseen has no short gaps left to reveal the
	///     fundamental, and its gaps concentrate on a multiple of the true period. Such a train is indistinguishable
	///     from a genuinely slower tick using gaps alone, and this estimator will confidently return the multiple. Do
	///     not build a harmonic/GCD estimator to recover it: sweeping every candidate period and scoring the residuals
	///     is degenerate on its own (the smaller the candidate, the better it fits, because every gap is within p/2 of
	///     some multiple), and read against the chance null (residuals of unrelated data are ~U(-p/2, p/2), rms
	///     p/sqrt(12)) the corpus's harmonic-looking trains fit no better than random gaps spanning the same range.
	/// </remarks>
	public static class DotIntervalEstimator
	{
		// A gap outside this range is not a tick of the spell being watched: below it two effects are resolving in
		// the same frame, above it a re-application after the debuff already expired. The upper bound is loose enough
		// not to clip any real measured interval.
		public const double MinGapSeconds = 0.2;
		public const double MaxGapSeconds = 30.0;

		// Every measured interval is a round half-second, so the estimate is snapped to one. This is not cosmetic:
		// it is what turns "2.16 s observed" into the 2000 ms retail actually uses, and the snap is only ever applied
		// to a value the acceptance rule already vouched for.
		public const uint QuantumMs = 500;

		// Two gaps are a coincidence; three are a cadence. Deliberately strict - a wrong interval is worse than no
		// interval, because no interval falls back to a documented default while a wrong one silently misprices
		// every tick of that spell.
		public const int MinGaps = 3;

		// The median acceptance test: at least ConcentrationShare of the gaps must sit within ConcentrationTolerance
		// of the median. This is a concentration test rather than a spread test, and the difference is load-bearing.
		// A quartile-ratio bound looks equivalent and is not: because missed ticks only ever lengthen gaps, a real
		// spell's upper quartile is routinely a whole doubled period out, so a spread bound rejects good data for the
		// very reason that data is good. Both constants sit on a wide plateau (tolerance 0.20-0.30, share 0.4-0.6 all
		// give the same answers), so nothing here is tuned to a knife edge.
		public const double ConcentrationTolerance = 0.25;
		public const double ConcentrationShare = 0.5;

		// The tight reading, which leads wherever it has one.
		//
		// The median is the wrong pivot once more than half the gaps are stretched: because the distortion is
		// one-sided, the median itself lands in the tail and the tight cluster at the bottom is measured against a
		// number it has nothing to do with. So this rule does not pivot on anything. It walks candidate intervals
		// upwards and takes the first with a tight cluster around it, subject to one more condition that is the whole
		// reason it is safe: the cluster must contain the shortest observed gap.
		//
		// That is the one-sidedness argument stated as a test. Nothing can shorten a gap below the true interval, so
		// the true interval is at the bottom of the distribution; a tight cluster with other gaps sitting below it is
		// therefore a harmonic of the period, not the period. Without that condition the rule accepts 4000 ms for a
		// ragged {2.1 2.2 2.4 | 4.0 4.2 4.4 | 8.5 8.8 9.1 | 12.0} train, which is exactly the shape the estimator
		// exists to refuse (RaggedGapsAreRejected). It is also why a naive densest-cluster rule is wrong: a wide
		// window swallows the real cluster and the stragglers above it.
		//
		// The cluster leads rather than merely falling back because a median over a stretched sample can land the
		// wrong side of a snap midpoint and turn a small bias into a whole quantum of error; a tight concentration
		// anchored where the one-sidedness argument says the period lives is better evidence. The constants sit on a
		// very wide plateau (tolerance 0.03-0.15, share 0.0-0.33 all give the same answers).
		//
		// It does not follow that the shortest gap bounds the interval from above, and that reasoning must not be
		// reintroduced as a shortcut: a re-application's first gap is an arbitrary fraction of the period, and most
		// accepted spells have an estimate above their own shortest gap. Only a tight cluster is evidence; a lone
		// short gap is not.
		public const double TightClusterTolerance = 0.05;
		public const double TightClusterShare = 0.25;

		/// <summary>
		///     The spell's tick interval in ms, or null when the gaps do not establish one.
		/// </summary>
		/// <param name="gapsSeconds">
		///     Every observed gap between consecutive ticks of one spell, pooled across captures (a gap is a duration,
		///     so pooling is safe - unlike the entity handles the gaps were derived from, which are per-session).
		/// </param>
		public static uint? Estimate(IEnumerable<double> gapsSeconds)
		{
			var gaps = gapsSeconds.Where(g => g is >= MinGapSeconds and <= MaxGapSeconds).OrderBy(g => g).ToList();
			if (gaps.Count < MinGaps)
				return null;

			var median = Median(gaps);
			if (median <= 0)
				return null;

			// The bottom-cluster reading leads wherever it has one, and the concentration test around the median is
			// what answers when it declines. Where the two disagree the median is the one that is wrong; the cluster
			// rule refuses rather than guesses, because a re-application gap below the cluster stops its walk.
			var byCluster = TightestBottomCluster(gaps);
			if (byCluster != null)
				return byCluster;

			var near = gaps.Count(g => Math.Abs(g - median) <= ConcentrationTolerance * median);
			return near < gaps.Count * ConcentrationShare
				? null
				: Snap(median);
		}

		// The tight reading described on TightClusterTolerance: the first candidate, walking upwards, whose tight
		// cluster is big enough and still holds the shortest gap. Once a candidate's window has climbed past that
		// shortest gap every larger one has too, so the walk stops there rather than running to the top.
		//
		// That stop is what makes declining the common outcome, and it is the right conservatism: a re-application
		// starts a fresh train whose first gap is an arbitrary fraction of the interval, and such a gap sits below
		// the real cluster. This rule cannot tell that fragment from a genuine shorter period, so it says nothing
		// and lets the median rule answer.
		private static uint? TightestBottomCluster(IReadOnlyList<double> sorted)
		{
			var needed = Math.Max(MinGaps, sorted.Count * TightClusterShare);
			foreach (var candidate in sorted)
			{
				var low = candidate * (1 - TightClusterTolerance);
				var high = candidate * (1 + TightClusterTolerance);
				if (sorted[0] < low)
					return null; // the window has left the bottom of the distribution behind

				var members = sorted.Where(g => g >= low && g <= high).ToList();
				if (members.Count >= needed)
					return Snap(members.Average());
			}

			return null;
		}

		private static uint? Snap(double seconds)
		{
			var snapped = (uint)(Math.Round(seconds * 1000 / QuantumMs) * QuantumMs);
			return snapped == 0
				? null
				: snapped;
		}

		private static double Median(IReadOnlyList<double> sorted)
		{
			var n = sorted.Count;
			return n % 2 == 1
				? sorted[n / 2]
				: (sorted[n / 2 - 1] + sorted[n / 2]) / 2;
		}
	}
}
