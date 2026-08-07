using System;
using System.Collections.Generic;
using System.Linq;

namespace Faolan.Importer.Captures
{
	// Turns the observed gaps between consecutive DoT ticks of ONE spell into that spell's tick interval, or into
	// "not measurable" (#437). Pure and separate from the capture walk so the acceptance rule is testable without a
	// corpus - the walk is trivial, the rule is where the judgement lives.
	//
	// The estimator exists because a capture is a LOSSY record of a periodic event. Two distortions matter, and they
	// are not symmetric:
	//
	//   * A dropped, delayed or unobserved tick can only ever LENGTHEN a gap - two 2 s ticks with the middle one
	//     missing read as one 4 s gap. Nothing can shorten one below the true interval.
	//   * A re-application (the caster recasts the DoT, or a second caster lands the same spell on the same target)
	//     starts a fresh tick train, so its first gap is an arbitrary fraction of the interval.
	//
	// So the raw gaps of a real DoT cluster tightly at the interval with a long upper tail, and the estimator's job
	// is to accept that shape and REJECT everything else - a spell seen ticking twice tells us nothing, and one whose
	// gaps are spread across the whole range is not being observed as a periodic tick at all.
	//
	// There are TWO acceptance rules, tried in order, and the second exists because the first pivots on the MEDIAN,
	// which the same one-sidedness defeats once more than half the gaps are stretched (#657 - see
	// TightClusterTolerance). The second never overrules the first; it only ever answers where the first said no.
	//
	// ⚠️ KNOWN LIMIT - the harmonic case. Because the distortion is one-sided, a spell whose ticks are MOSTLY unseen
	// has no short gaps left to reveal the fundamental, and its gaps concentrate on a MULTIPLE of the true period.
	// Such a train is indistinguishable from a genuinely slower tick using gaps alone, and this estimator will
	// confidently return the multiple. One spell in the corpus looks like this: Aura: Fire Weave, whose 17 gaps
	// cluster at 0.92 / 1.23 / 1.53 / 1.84 - spaced by ~0.3 s, i.e. plausibly a ~0.3 s pulse seen 3-6 ticks at a
	// time.
	//
	// ⚠️ **That ~0.3 s reading does not survive a significance test, so do NOT build a harmonic/GCD estimator on
	// it** (#524). Sweeping every candidate period and scoring the residuals is degenerate on its own - the smaller
	// the candidate, the better it fits, because every gap is within p/2 of some multiple - so the score has to be
	// read against what CHANCE gives: residuals of unrelated data are ~U(-p/2, p/2), i.e. rms = p/sqrt(12). Against
	// that null the best fit these 17 gaps admit is a ratio of **0.66** (at 155 / 310 ms), and running the identical
	// sweep over RANDOM gaps spanning the same range finds a median best of **0.70**, with 5% of trials reaching
	// 0.57. So 0.66 sits inside the noise: the gaps contain no recoverable fundamental, and a harmonic estimator
	// would be fitting the sweep's own degeneracy rather than a period.
	//
	// The measured 1500 ms therefore stands as the only supported value, and it is what the seed carries. (The
	// disagreement the issue recorded is gone - the hand scan's 500 ms was never supported by these gaps either,
	// nothing sitting near it.)
	public static class DotIntervalEstimator
	{
		// A gap outside this range is not a tick of the spell we are watching: below it we are looking at two effects
		// resolving in the same frame, above it at a re-application after the debuff already expired. The corpus's
		// measured intervals run 500 ms to 11 s, so 30 s is loose enough not to clip a real one.
		public const double MinGapSeconds = 0.2;
		public const double MaxGapSeconds = 30.0;

		// Every measured interval in the corpus is a round half-second, so the estimate is snapped to one. This is
		// not a cosmetic tidy-up: it is what turns "2.16 s observed" into the 2000 ms retail actually used, and the
		// snap is only ever applied to a median the acceptance rule already vouched for.
		public const uint QuantumMs = 500;

		// Two gaps are a coincidence; three are a cadence. Deliberately strict - a wrong interval is worse than no
		// interval, because no interval falls back to a documented default while a wrong one silently misprices
		// every tick of that spell.
		public const int MinGaps = 3;

		// The acceptance test: at least ConcentrationShare of the gaps must sit within ConcentrationTolerance of the
		// median. This is a CONCENTRATION test rather than a spread test, and the difference is load-bearing. A
		// quartile-ratio bound looks equivalent and is not: because missed ticks only ever lengthen gaps, a real
		// spell's upper quartile is routinely a whole doubled period out, so a spread bound rejects good data for
		// the very reason that data is good. Fires of Gehenna (70 gaps, p25 2.15 / median 2.20 / p75 3.09) is the
		// worked example - unmistakably a 2 s tick, rejected by a 1.35 quartile ratio, accepted here.
		//
		// Both constants sit on a wide plateau: every combination of tolerance 0.20-0.30 and share 0.4-0.6 gives the
		// same answer on the corpus (34-35 of the 38 hand-measured #437 intervals reproduced, the same 15 new spells
		// measured), so nothing here is tuned to a knife edge.
		public const double ConcentrationTolerance = 0.25;
		public const double ConcentrationShare = 0.5;

		// The TIGHT reading, which leads wherever it has one (#657 introduced it as a fallback; #660 promoted it).
		//
		// The median is the wrong pivot once MORE THAN HALF the gaps are stretched: because the distortion is
		// one-sided, the median itself lands in the tail and the tight cluster at the bottom is measured against a
		// number it has nothing to do with. Ice Strike (Rank 6) is the worked case - 13 of its 38 gaps sit inside
		// 3.5% of each other at ~2.15 s, but its median is 4.20 s, so only 6 gaps are "near" it and it is rejected.
		// The signal is there and the median rule looks past it.
		//
		// So this rule does not pivot on anything. It walks candidate intervals UPWARDS and takes the first with a
		// TIGHT cluster around it, subject to one more condition that is the whole reason it is safe:
		//
		//   ⚠️ the cluster must contain the SHORTEST observed gap.
		//
		// That is the one-sidedness argument stated as a test. Nothing can shorten a gap below the true interval, so
		// the true interval is at the BOTTOM of the distribution; a tight cluster with other gaps sitting below it is
		// therefore a HARMONIC of the period, not the period. Without that condition the rule accepts 4000 ms for a
		// ragged {2.1 2.2 2.4 | 4.0 4.2 4.4 | 8.5 8.8 9.1 | 12.0} train, which is exactly the shape the estimator
		// exists to refuse (RaggedGapsAreRejected). It is also why a naive densest-cluster rule is wrong and was not
		// used: with a ±25% window the densest cluster of Ice Strike's gaps sits at 2.76 s, because that window
		// swallows the real cluster AND the stragglers above it.
		//
		// Measured over the whole corpus (`--report dot-trains --out <file>` writes every gap at full precision), this
		// rescues Ice Strike (Rank 6) at 2000 ms and CORRECTS Ice Strike (Rank 1) from 2500 to 2000. The constants sit
		// on a very wide plateau: every combination of tolerance 0.03-0.15 and share 0.0-0.33 gives the same answers.
		//
		// ⚠️ Rank 1 is the case that says this rule must LEAD rather than merely fall back, and it is worth spelling
		// out because #657 shipped it the other way round. 42 of Rank 1's 93 gaps sit inside ±5% of ~2.15 s - the same
		// cluster, to two decimals, that Rank 6 shows - while its median is 2.365 s. That median is only 10% above the
		// cluster, but 2.365 lands the wrong side of the 2.25 s midpoint, so the half-second snap turns a 10% bias
		// into a 25% error and reports 2500. A tighter concentration test anchored where the one-sidedness argument
		// says the period lives is simply better evidence than a median over a stretched sample, so where both speak
		// the cluster wins.
		//
		// ⚠️ It does NOT follow that the shortest gap bounds the interval from above, and that reasoning must not be
		// reintroduced as a shortcut: a re-application's first gap is an arbitrary FRACTION of the period, and 41 of
		// the 50 accepted spells have an estimate above their own shortest gap. Only a tight CLUSTER is evidence; a
		// lone short gap is not.
		public const double TightClusterTolerance = 0.05;
		public const double TightClusterShare = 0.25;

		// The spell's tick interval in ms, or null when the gaps do not establish one. `gapsSeconds` is every
		// observed gap between consecutive ticks of one spell, pooled across captures (a gap is a duration, so
		// pooling is safe - unlike the entity handles the gaps were derived from, which are per-session).
		public static uint? Estimate(IEnumerable<double> gapsSeconds)
		{
			var gaps = gapsSeconds.Where(g => g is >= MinGapSeconds and <= MaxGapSeconds).OrderBy(g => g).ToList();
			if (gaps.Count < MinGaps)
				return null;

			var median = Median(gaps);
			if (median <= 0)
				return null;

			// The bottom-cluster reading LEADS wherever it has one, and the concentration test around the median is
			// what answers when it declines (#660). That ordering is measured, not a preference: over the corpus's 50
			// median-accepted spells the two agree on 21, the cluster rule declines on 28 - it refuses rather than
			// guesses, because a re-application gap below the cluster stops its walk - and they disagree on exactly
			// ONE, `Ice Strike (Rank 1)`, where the median is the one that is wrong.
			var byCluster = TightestBottomCluster(gaps);
			if (byCluster != null)
				return byCluster;

			var near = gaps.Count(g => Math.Abs(g - median) <= ConcentrationTolerance * median);
			return near < gaps.Count * ConcentrationShare
				? null
				: Snap(median);
		}

		// The tight reading described on TightClusterTolerance: the first candidate, walking upwards, whose tight
		// cluster is big enough AND still holds the shortest gap. Once a candidate's window has climbed past that
		// shortest gap every larger one has too, so the walk stops there rather than running to the top.
		//
		// ⚠️ That stop is what makes declining the COMMON outcome (28 of 50), and it is the right conservatism: a
		// re-application starts a fresh train whose first gap is an arbitrary FRACTION of the interval, and such a
		// gap sits below the real cluster. This rule cannot tell that fragment from a genuine shorter period, so it
		// says nothing and lets the median rule answer.
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
