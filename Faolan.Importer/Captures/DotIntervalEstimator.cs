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
	// ⚠️ KNOWN LIMIT - the harmonic case. Because the distortion is one-sided, a spell whose ticks are MOSTLY unseen
	// has no short gaps left to reveal the fundamental, and its gaps concentrate on a MULTIPLE of the true period.
	// Such a train is indistinguishable from a genuinely slower tick using gaps alone, and this estimator will
	// confidently return the multiple. One spell in the corpus looks like this: Aura: Fire Weave, whose 17 gaps
	// cluster at 0.92 / 1.23 / 1.53 / 1.84 - spaced by ~0.3 s, i.e. plausibly a ~0.3 s pulse seen 3-6 ticks at a
	// time, which is neither the 500 ms the hand scan recorded nor the 1500 ms the median here yields. It is left
	// disagreeing rather than special-cased; see the Gitea issue.
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

			var near = gaps.Count(g => Math.Abs(g - median) <= ConcentrationTolerance * median);
			if (near < gaps.Count * ConcentrationShare)
				return null;

			var snapped = (uint)(Math.Round(median * 1000 / QuantumMs) * QuantumMs);
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
