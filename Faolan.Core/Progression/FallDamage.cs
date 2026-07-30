using System;

namespace Faolan.Core.Progression
{
	/// <summary>
	///     Fall damage and the debuff that rides with it (#495 / #504) - the first NON-COMBAT damage source in the
	///     server, so it deliberately does not go through <c>CombatResolver</c>: retail never sends a
	///     <c>DamageResultIIR</c> for a fall.
	/// </summary>
	/// <remarks>
	///     <para>
	///         Measured over the WHOLE corpus, not one capture: 28 fall events in 9 of the 119 dumps, across four
	///         characters whose health pools span 232 to 8241 - a 35x range, which is what makes the pool-relative
	///         reading below separable from a flat one. Every one arrives as a <c>StatDamageIIR</c> (0xE9F24184) in the
	///         ENVIRONMENTAL form (see <c>CombatEffectBuilder.EnvironmentalDamage</c>): stat 0x01B (Health),
	///         <c>school = 12</c>, and no caster / spell / idx at all.
	///     </para>
	///     <para>
	///         <b>Damage is a fraction of MAX HEALTH, not a flat number.</b> At the same fall distance a level-80
	///         necromancer (pool 6341) takes ~345 and a Tortage-beach Herald of Xotli (pool 263) takes ~32 - a 10x gap
	///         that tracks the pools, not the height. Fitting the 24 events whose airborne segment is resolvable gives
	///         <c>damage = maxHealth * 0.038 * (fallDistance - 3.08)</c>, i.e. <b>3.8% of max health per unit of fall
	///         beyond ~3 units</b>. The intercept is pinned by the smallest DAMAGING fall observed (3.10 units, which
	///         cost 30.6 health) rather than by the fit, which would put it at 4.5 and predict zero for that one.
	///     </para>
	///     <para>
	///         Accuracy, stated plainly: over the ten falls of 7 units or more the median error is <b>13%</b> and nine
	///         of ten are within 30%; one is 65% high. Below 7 units the residuals blow up entirely (a 4-unit fall can
	///         predict +200%), and that is a MEASUREMENT limit rather than a wrong model - the drop is reconstructed
	///         from position samples ~200 ms apart, so it misses a fraction of a unit at each end, and that fraction
	///         dominates when the fall is 4 units and the free distance is 3.08.
	///     </para>
	///     <para>
	///         The client's own reported impact SPEED orders the 24 events far more cleanly than the sampled height
	///         does - it is the vertical velocity the landing <c>CharDCMove</c> carries, and it separates the debuff
	///         tiers almost perfectly where the sampled height does not. A future pass driving this off velocity should
	///         tighten every constant here. Kept on distance because distance is what the server can compute from state
	///         it already tracks, without trusting a client-reported speed.
	///     </para>
	///     <para>
	///         <b>The debuff tier is a function of the damage as a fraction of max health</b>, which separates the 24
	///         observations where raw fall distance does not (a 5.44-unit fall took no debuff while a 4.89-unit fall
	///         took one - the deeper of the two was the shorter drop). Measured bands:
	///         no debuff at or below 8.04%, <b>Bruised Body</b> from 7.98% to 34.56%, <b>Broken Bones</b> from 58.63%
	///         to 64.85%.
	///     </para>
	///     <para>
	///         ⚠️ The Bruised-Body boundary is not merely tight, it is <b>INVERTED</b>, and no threshold can reproduce
	///         all 24: the highest no-debuff observation is <b>8.042%</b> of max health while the lowest Bruised Body one
	///         is <b>7.977%</b> - the deeper-hurting fall is the one that took no debuff. A threshold placed BETWEEN them
	///         therefore gets BOTH wrong; one placed below or above both gets exactly one wrong.
	///         <see cref="BruisedBodyFraction" /> is set just under 7.977% so the real Bruised Body case is reproduced
	///         and the single error is a borderline fall gaining a debuff it did not have - the gentler direction, since
	///         Bruised Body carries no stat effects at all. `FallDamageTests` asserts 23 of 24 and names the exception.
	///         The Broken Bones boundary has a wide empty band (34.6% to 58.6%) and is set inside it.
	///     </para>
	///     <para>
	///         Both debuffs already ship in <c>faolan.db</c> with their durations and stat effects, so applying them
	///         through the ordinary buff path gets the effects for free (#367): <b>Bruised Body</b> (3744225, 5 s) has
	///         NO stat effects at all - it is a pure marker/animation - and <b>Broken Bones</b> (3745876, 10 s) carries
	///         Evade Chance -5000 and Casting Concentration -800.
	///     </para>
	/// </remarks>
	public static class FallDamage
	{
		/// <summary>
		///     The <c>DamageSchool</c>-slot value every captured fall carries - <c>DamageSchool.Environmental</c>.
		///     Outside the range the real schools occupy, which is why it reads as "the world did this" rather than an
		///     element. (#487 recovered that range: the schools run 1..10 with Cold appended at 10, not inserted at 5.)
		/// </summary>
		public const uint EnvironmentalSchool = (uint)Enums.DamageSchool.Environmental;

		/// <summary>Wire-observed fall debuffs (both already in <c>Spells</c> with their real durations).</summary>
		public const uint BruisedBodySpellId = 3744225;

		public const uint BrokenBonesSpellId = 3745876;

		/// <summary>Falls shorter than this cost nothing - the fitted intercept (3.08), rounded to the sample noise.</summary>
		public const float FreeFallDistance = 3.08f;

		/// <summary>Fraction of max health lost per unit of fall beyond <see cref="FreeFallDistance" />.</summary>
		public const float DamagePerUnit = 0.038f;

		/// <summary>
		///     Damage (as a fraction of max health) at or above which the fall applies <b>Bruised Body</b>. Just under
		///     the lowest observed Bruised Body fall (7.977%) - see the inversion note in the remarks for why it is not
		///     a round 8%.
		/// </summary>
		public const float BruisedBodyFraction = 0.0795f;

		/// <summary>Damage (as a fraction of max health) at or above which it applies <b>Broken Bones</b> instead.</summary>
		public const float BrokenBonesFraction = 0.46f;

		/// <summary>
		///     The health cost of a fall of <paramref name="fallDistance" /> world units for a character whose maximum
		///     health is <paramref name="maxHealth" />. 0 for any fall inside the free distance.
		/// </summary>
		public static uint Resolve(float fallDistance, uint maxHealth)
		{
			if (fallDistance <= FreeFallDistance || maxHealth == 0)
				return 0;

			var damage = maxHealth * DamagePerUnit * (fallDistance - FreeFallDistance);
			// A fall CAN kill: the corpus tops out at 64.9% of max health, so nothing here clamps below the pool. Round
			// rather than truncate so a fall just past the threshold costs 1 rather than 0.
			return (uint)MathF.Round(MathF.Min(damage, maxHealth));
		}

		/// <summary>
		///     The debuff the landing applies, or 0 for a fall that hurt too little to leave one. Keyed on the DAMAGE
		///     relative to the pool, not the distance - see the remarks on <see cref="FallDamage" />.
		/// </summary>
		public static uint ResolveDebuff(uint damage, uint maxHealth)
		{
			if (maxHealth == 0 || damage == 0)
				return 0;

			var fraction = (float)damage / maxHealth;
			if (fraction >= BrokenBonesFraction)
				return BrokenBonesSpellId;

			return fraction >= BruisedBodyFraction ? BruisedBodySpellId : 0;
		}
	}
}
