using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Progression;
using Xunit;

namespace Faolan.Tests
{
	// Gates the fall-damage curve and the fall-debuff ladder (#495/#504) against EVERY fall the corpus contains: 24
	// events (of 28 school-12 StatDamage records; four have no resolvable airborne segment) in 9 of the 119 dumps,
	// across FOUR characters whose health pools span 263 to 8241.
	//
	// That pool spread is what the table is for. At a comparable fall a level-80 necromancer (pool 6341) takes ~345
	// health and a Tortage-beach Herald of Xotli (pool 263) takes ~32 - so the damage cannot be flat, and modelling it
	// as a fraction of MAX HEALTH is not a guess but the only reading the observations allow.
	//
	// Values are at WIRE scale (x100) exactly as the capture carries them, so the table can be re-derived from a dump
	// without a conversion step in between.
	public class FallDamageTests
	{
		// (fall distance in world units, max health x100, observed damage x100, observed debuff spell)
		public static readonly IReadOnlyList<object[]> Observations =
		[
			[3.10f, 651800u, 3060u, 0u],
			[4.02f, 26300u, 1305u, 0u], // Hoxxling, a level-~5 pool of 263 - the low end of the 31x spread
			[4.13f, 634100u, 14076u, 0u],
			[4.20f, 824100u, 37920u, 0u],
			[4.58f, 634100u, 15586u, 0u],
			[4.84f, 651800u, 24646u, 0u],
			[5.36f, 634100u, 42083u, 0u],
			[5.44f, 634100u, 50994u, 0u], // ⚠️ the INVERTED pair's no-debuff half, at 8.042% of pool
			[5.57f, 651800u, 19016u, 0u],
			[5.81f, 651800u, 26192u, 0u],
			[6.19f, 824100u, 65737u, FallDamage.BruisedBodySpellId], // ⚠️ ... and its Bruised half, at 7.977%
			[6.21f, 612600u, 42458u, 0u],
			[6.23f, 651800u, 34573u, 0u],
			[6.38f, 26300u, 3190u, FallDamage.BruisedBodySpellId], // the 263-pool character DOES get the debuff
			[7.02f, 634100u, 111481u, FallDamage.BruisedBodySpellId],
			[7.98f, 612600u, 98090u, FallDamage.BruisedBodySpellId],
			[8.18f, 824100u, 96764u, FallDamage.BruisedBodySpellId],
			[8.30f, 651800u, 117766u, FallDamage.BruisedBodySpellId],
			[8.45f, 612600u, 166932u, FallDamage.BruisedBodySpellId],
			[8.91f, 824100u, 150721u, FallDamage.BruisedBodySpellId],
			[9.91f, 634100u, 184276u, FallDamage.BruisedBodySpellId],
			[11.86f, 634100u, 219125u, FallDamage.BruisedBodySpellId],
			[16.82f, 651800u, 382176u, FallDamage.BrokenBonesSpellId],
			[19.86f, 824100u, 534437u, FallDamage.BrokenBonesSpellId]
		];

		// The debuff tier reproduced from each fall's OBSERVED damage. Deliberately fed the observed damage rather than
		// our own predicted damage: this asserts the TIER rule in isolation, so a drift in the damage curve cannot make
		// this test pass or fail for the wrong reason.
		//
		// 23 of 24 must match exactly. The one that cannot is the inverted pair documented on FallDamage: the 5.44-unit
		// fall took 8.042% of its pool and NO debuff while the 6.19-unit fall took 7.977% and DID get one, so the
		// higher-damage observation is the undebuffed one and no threshold reproduces both.
		[Fact]
		public void DebuffTier_ReproducesEveryCorpusObservation_ExceptTheOneInversion()
		{
			var mismatches = new List<string>();
			foreach (var row in Observations)
			{
				var (fall, pool, damage, expected) = ((float)row[0], (uint)row[1], (uint)row[2], (uint)row[3]);
				var actual = FallDamage.ResolveDebuff(damage, pool);
				if (actual != expected)
					mismatches.Add($"fall {fall:F2} ({100f * damage / pool:F3}% of pool): expected {expected}, got {actual}");
			}

			// Named exactly, so a NEW disagreement fails loudly instead of hiding inside an allowance.
			Assert.Equal(["fall 5.44 (8.042% of pool): expected 0, got 3744225"], mismatches);
		}

		// The damage curve, gated only on the falls big enough to MEASURE (>= 7 units). Below that the sampled fall
		// distance is the limiting factor, not the model: the drop is reconstructed from position samples ~200 ms apart,
		// which misses a fraction of a unit at each end, and that fraction dominates a 4-unit fall whose free distance
		// is 3.08. Gating on the short falls would be gating on my sampling error.
		//
		// Asserted as a DISTRIBUTION rather than a worst case, because a worst case here would be asserting precision
		// the measurement does not have: the median error is ~13% and nine of the ten are within 30%, with a single
		// 65%-high outlier (the 8.18-unit fall on the 8241 pool, whose observed 967 sits below what the same character's
		// 8.91-unit fall cost - i.e. that row's apex is the suspect part, not the curve).
		[Fact]
		public void Damage_TracksTheCorpusCurve_OnFallsBigEnoughToMeasure()
		{
			var errors = new List<(float Error, string Row)>();
			foreach (var row in Observations)
			{
				var (fall, pool, damage) = ((float)row[0], (uint)row[1], (uint)row[2]);
				if (fall < 7f)
					continue;

				var predicted = FallDamage.Resolve(fall, pool);
				// Cast BEFORE subtracting: both are uint, so `predicted - damage` underflows to ~4e9 whenever the
				// prediction is the smaller of the two, and every error reads as a few million percent.
				var error = MathF.Abs((float)predicted - damage) / damage;
				errors.Add((error, $"fall {fall:F2}, pool {pool}: observed {damage}, predicted {predicted} ({error:P0} off)"));
			}

			Assert.Equal(10, errors.Count);

			var sorted = errors.Select(e => e.Error).OrderBy(e => e).ToArray();
			var median = (sorted[4] + sorted[5]) / 2f;
			Assert.True(median < 0.20f, $"the fall-damage curve drifted - median error {median:P0}; worst row {errors.MaxBy(e => e.Error).Row}");

			var within30 = errors.Count(e => e.Error < 0.30f);
			Assert.True(within30 >= 9, $"only {within30} of 10 falls within 30%; worst row {errors.MaxBy(e => e.Error).Row}");
		}

		// Damage is POOL-RELATIVE, and this is the assertion that actually pins that rather than assuming it. Two
		// observations at almost the same fall distance (6.19 and 6.38 units) come from characters 31x apart in health,
		// and their damages differ by very nearly the same 31x - which a flat-damage model cannot produce.
		[Fact]
		public void Damage_ScalesWithTheHealthPool_NotWithDistanceAlone()
		{
			var big = Observations.Single(r => (float)r[0] == 6.19f);
			var small = Observations.Single(r => (float)r[0] == 6.38f);

			var poolRatio = (float)(uint)big[1] / (uint)small[1];
			var damageRatio = (float)(uint)big[2] / (uint)small[2];

			Assert.True(poolRatio > 25f, $"the two characters should be far apart in health; got {poolRatio:F1}x");

			// The claim being pinned is "pool-relative, not flat", so assert the damage ratio is far nearer the POOL
			// ratio than it is to 1 - not that it matches the pool ratio to some tight tolerance, which the sampled fall
			// distances could not support. A flat-damage model predicts ~1 here; the observations give ~20.
			Assert.True(damageRatio > 10f, $"a flat-damage model would put this near 1; got {damageRatio:F1}x");
			Assert.InRange(damageRatio / poolRatio, 0.5f, 2f);
		}

		// A fall inside the free distance costs nothing at all, however big the pool.
		[Fact]
		public void ShortFalls_CostNothing()
		{
			Assert.Equal(0u, FallDamage.Resolve(0f, 6341));
			Assert.Equal(0u, FallDamage.Resolve(FallDamage.FreeFallDistance, 6341));
			Assert.Equal(0u, FallDamage.ResolveDebuff(0, 6341));
		}

		// A fall can kill, but never overshoot: the health cost is clamped to the pool, so nothing downstream has to
		// cope with a negative-health entity.
		[Fact]
		public void HugeFall_IsClampedToThePool()
		{
			Assert.Equal(6341u, FallDamage.Resolve(10000f, 6341));
		}

		// A pool of 0 (an entity whose vitals were never seeded) must not produce damage or a division by zero.
		[Fact]
		public void ZeroPool_IsInert()
		{
			Assert.Equal(0u, FallDamage.Resolve(50f, 0));
			Assert.Equal(0u, FallDamage.ResolveDebuff(100, 0));
		}
	}
}
