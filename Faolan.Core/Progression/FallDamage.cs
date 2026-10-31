using System;

namespace Faolan.Core.Progression
{
	/// <summary>
	///     Fall damage and the debuff that rides with it - the one non-combat damage source in the server, so it
	///     deliberately does not go through <c>CombatResolver</c>: retail never sends a <c>DamageResultIIR</c> for a
	///     fall.
	/// </summary>
	/// <remarks>
	///     <para>
	///         Measured over the whole corpus, not one capture: 28 fall events in 9 of the 119 dumps, across four
	///         characters whose health pools span 232 to 8241 - a 35x range, which is what makes the pool-relative
	///         reading below separable from a flat one. Every one arrives as a <c>StatDamageIIR</c> (0xE9F24184) in
	///         the environmental form (see <c>CombatEffectBuilder.EnvironmentalDamage</c>): stat 0x01B (Health),
	///         <c>school = 12</c>, and no caster, spell or idx at all.
	///     </para>
	///     <para>
	///         Damage is a fraction of max health, not a flat number. At the same fall distance a level-80 necromancer
	///         (pool 6341) takes ~345 and a Tortage-beach Herald of Xotli (pool 263) takes ~32 - a 10x gap that tracks
	///         the pools, not the height. Fitting the 24 events whose airborne segment is resolvable gives
	///         <c>damage = maxHealth * 0.038 * (fallDistance - 3.08)</c>, i.e. 3.8% of max health per unit of fall
	///         beyond ~3 units. The intercept is pinned by the smallest damaging fall observed (3.10 units, which cost
	///         30.6 health) rather than by the fit, which would put it at 4.5 and predict zero for that one.
	///     </para>
	///     <para>
	///         Accuracy, stated plainly: over the ten falls of 7 units or more the median error is 13% and nine of ten
	///         are within 30%; one is 65% high. Below 7 units the residuals blow up entirely (a 4-unit fall can
	///         predict +200%), and that is a measurement limit rather than a wrong model - the drop is reconstructed
	///         from position samples ~200 ms apart, so it misses a fraction of a unit at each end, and that fraction
	///         dominates when the fall is 4 units and the free distance is 3.08.
	///     </para>
	///     <para>
	///         The client's own reported impact speed orders the events far better than the sampled height does. It is
	///         the vertical velocity the landing <c>CharDCMove</c> carries. Over the 39 falls
	///         <c>--report fall-speed</c> pairs, the rank correlation against damage, per entity so the pool is fixed:
	///     </para>
	///     <para>
	///         <code>
	///         handle       falls   speed vs damage   drop vs damage
	///         0x0802E5D4      30             0.886            0.460
	///         0x110099BD       5             1.000            0.700
	///         </code>
	///     </para>
	///     <para>
	///         Driving damage off a client-reported speed does not trust the client for anything new: the server's own
	///         fall height is reconstructed from the client's position stream, so the velocity in the same packet is
	///         the same source, one field over - and without the 200 ms sampling loss.
	///     </para>
	///     <para>
	///         The pool is the high-water mark of <c>HealthCurrent</c> for that handle up to the fall, and it is
	///         neither of the two things that look like it: <c>StatDamageIIR.StatValue</c> is the damage again on the
	///         environmental form (every fall then costs exactly 50% by construction), and the <c>HealthMax</c> the
	///         same stat block states is a base the client tops up - it reads 2,371 for every fall in the corpus, the
	///         same value for two different characters, against which the deepest falls come out at 225% of the pool.
	///         <c>Delta</c> is also in hundredths.
	///     </para>
	///     <para>
	///         The law is fitted in v-squared, which needs no gravity at all. Expressing it in height requires
	///         dividing by a measured gravity, and gravity can only be measured as <c>v^2 / 2h</c> against the sampled
	///         drop - the very quantity this exists to stop using (over the deep falls it runs p10 7.6 / median 19.3 /
	///         p90 36.2, so the conversion is the loosest link in the chain). But <c>frac = a(h - h0)</c> and
	///         <c>frac = A(v^2 - V0)</c> are the same model under <c>v^2 = 2gh</c>, with gravity absorbed into the
	///         fitted slope (<c>A = a/2g</c>, <c>V0 = 2g*h0</c>). Fit in v-squared, apply to the speed the client
	///         reports, and the conversion never happens.
	///     </para>
	///     <para>
	///         Measured over the same 34 falls, all three predictors scored the same way (<c>--report fall-speed</c>):
	///     </para>
	///     <para>
	///         <code>
	///         correlation with the damage fraction    v² 0.957    v 0.937    sampled drop 0.625
	///         |error|, best case for each             v² p50 21%              drop p50 40% (refitted on this corpus)
	///         </code>
	///     </para>
	///     <para>
	///         So <see cref="Resolve" /> is the speed law and <see cref="ResolveFromDistance" /> the fallback for a
	///         landing whose move carried no velocity block. The fitted intercept (13.96 u/s) sits above the smallest
	///         damaging fall in the corpus (12.21 u/s, which cost 1.3% of pool), so that one fall is predicted free.
	///         Pinning the intercept there instead costs accuracy on every other fall (p50 31% against 21%), and the
	///         error it removes is a trivial fall costing nothing rather than 1.3%, which is the forgiving direction.
	///     </para>
	///     <para>
	///         Two pairing rules the report needs and a naive pass does not have: the environmental school carries
	///         lava as well as falls, so a hit whose nearest landing is more than a second away is not a fall (the
	///         corpus has one 94 s out); and the fall debuff cannot be attributed by a time bound alone - at 5 s the
	///         corpus yields zero Broken Bones and at 15 s it yields five spanning 0.47% to 84% of the pool, i.e. the
	///         next fall's debuff. The report prints the debuff per fall and deliberately does not band it.
	///     </para>
	///     <para>
	///         The debuff tier is a function of the damage as a fraction of max health, which separates the 24
	///         observations where raw fall distance does not (a 5.44-unit fall took no debuff while a 4.89-unit fall
	///         took one - the deeper of the two was the shorter drop). Measured bands: no debuff at or below 8.04%,
	///         Bruised Body from 7.98% to 34.56%, Broken Bones from 58.63% to 64.85%.
	///     </para>
	///     <para>
	///         The Bruised-Body boundary is not merely tight, it is inverted, and no threshold can reproduce all 24:
	///         the highest no-debuff observation is 8.042% of max health while the lowest Bruised Body one is 7.977% -
	///         the deeper-hurting fall is the one that took no debuff. A threshold placed between them therefore gets
	///         both wrong; one placed below or above both gets exactly one wrong.
	///         <see cref="BruisedBodyFraction" /> is set just under 7.977% so the real Bruised Body case is reproduced
	///         and the single error is a borderline fall gaining a debuff it did not have - the gentler direction,
	///         since Bruised Body carries no stat effects at all. <c>FallDamageTests</c> asserts 23 of 24 and names
	///         the exception. The Broken Bones boundary has a wide empty band (34.6% to 58.6%) and is set inside it.
	///     </para>
	///     <para>
	///         Both debuffs already ship in <c>faolan.sql</c> with their durations and stat effects, so applying them
	///         through the ordinary buff path gets the effects for free: Bruised Body (3744225, 5 s) has no stat
	///         effects at all - it is a pure marker/animation - and Broken Bones (3745876, 10 s) carries Evade Chance
	///         -5000 and Casting Concentration -800.
	///     </para>
	/// </remarks>
	public static class FallDamage
	{
		/// <summary>
		///     The <c>DamageSchool</c>-slot value every captured fall carries - <c>DamageSchool.Environmental</c>.
		///     Outside the range the real schools occupy, which is why it reads as "the world did this" rather than an
		///     element. The schools run 1..10 with Cold appended at 10, not inserted at 5.
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
		///     A landing slower than this costs nothing - the speed law's own intercept, and the same statement
		///     <see cref="FreeFallDistance" /> makes in the other unit (13.96 u/s is 5.05 units at the corpus's
		///     median gravity).
		/// </summary>
		public const float FreeFallSpeed = 13.96f;

		/// <summary>
		///     Fraction of max health lost per unit of <c>v^2 - FreeFallSpeed^2</c>. Fitted over the same 34 falls as
		///     everything else here; see the remarks for why v-squared and not v (the law is linear in the square of
		///     the impact speed, because that is what is linear in height).
		/// </summary>
		public const float DamagePerSpeedSquared = 0.00067221f;

		/// <summary>
		///     Damage (as a fraction of max health) at or above which the fall applies Bruised Body. Just under the
		///     lowest observed Bruised Body fall (7.977%) - see the inversion note in the remarks for why it is not a
		///     round 8%.
		/// </summary>
		public const float BruisedBodyFraction = 0.0795f;

		/// <summary>Damage (as a fraction of max health) at or above which it applies Broken Bones instead.</summary>
		public const float BrokenBonesFraction = 0.46f;

		/// <summary>
		///     The health cost of a landing at <paramref name="impactSpeed" /> world units per second (a positive
		///     number - the magnitude of the downward velocity the client reported) for a character whose maximum
		///     health is <paramref name="maxHealth" />. 0 for any landing at or below <see cref="FreeFallSpeed" />.
		///     <para>
		///         This is the primary path: the speed comes off the landing move, where
		///         <see cref="ResolveFromDistance" />'s height is reconstructed from 200 ms-apart position samples
		///         that lose a fraction of a unit at each end. Same corpus, same 34 falls: <c>v^2</c> explains the
		///         damage at r = 0.96 against the sampled drop's 0.63.
		///     </para>
		/// </summary>
		public static uint Resolve(float impactSpeed, uint maxHealth)
		{
			if (impactSpeed <= FreeFallSpeed || maxHealth == 0)
				return 0;

			var damage = maxHealth * DamagePerSpeedSquared * (impactSpeed * impactSpeed - FreeFallSpeed * FreeFallSpeed);
			return (uint)MathF.Round(MathF.Min(damage, maxHealth));
		}

		/// <summary>
		///     The health cost of a fall of <paramref name="fallDistance" /> world units for a character whose maximum
		///     health is <paramref name="maxHealth" />. 0 for any fall inside the free distance.
		///     <para>
		///         The fallback, for a landing whose move carried no velocity block. It is the same law in the other
		///         unit and its constants are unchanged; what differs is the accuracy of the input.
		///     </para>
		/// </summary>
		public static uint ResolveFromDistance(float fallDistance, uint maxHealth)
		{
			if (fallDistance <= FreeFallDistance || maxHealth == 0)
				return 0;

			var damage = maxHealth * DamagePerUnit * (fallDistance - FreeFallDistance);
			// A fall can kill: the corpus tops out at 64.9% of max health, so nothing here clamps below the pool.
			// Round rather than truncate so a fall just past the threshold costs 1 rather than 0.
			return (uint)MathF.Round(MathF.Min(damage, maxHealth));
		}

		/// <summary>
		///     The debuff the landing applies, or 0 for a fall that hurt too little to leave one. Keyed on the damage
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
