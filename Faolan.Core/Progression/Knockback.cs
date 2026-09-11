using System;
using System.Numerics;

namespace Faolan.Core.Progression
{
	/// <summary>
	///     Knockback: the server shoving an entity along a segment it did not ask to walk. Pure geometry plus
	///     resistance, so it is testable without a world.
	/// </summary>
	/// <remarks>
	///     <para>
	///         The wire side is decoded and byte-exact (<c>MoveTaskCodec.BuildForcedMoveBody</c>, mode 0x05, with a
	///         <c>SetPosIIR</c> pinning the destination). What follows is the part that is not decoded, and the
	///         distinction matters when reading these constants.
	///     </para>
	///     <para>
	///         The corpus contains two knockbacks. One displaced the player 3.01 units and came with an
	///         <c>ApplySpell</c> of spell 4784832, which the database names "Knockback immunity" (10 ms) - the
	///         mechanic naming itself. The other, an inbound knockback on the player from a <c>Wharf Rat Cutthroat</c>
	///         [V2: NecroL80_mage-necromancer_login_gameplay_annotated_3.tshark.json frame 7790, annotated "got
	///         knockedback"], measures 3.000 horizontal - and the attacker-to-victim distance goes 3.255 to 6.255,
	///         exactly the displacement. Two independent events therefore settle both questions: the distance is a
	///         constant 3.0 rather than something scaled per ability or per level, and the push is directly along the
	///         attacker-to-victim ray, not along the attacker's facing (which a single head-on sample could not
	///         separate).
	///     </para>
	///     <para>
	///         The batch is bigger than the immunity buff. Retail sends, in one instant: <c>Knockback immunity</c> and
	///         <c>Recently Impaired</c> on the victim (the latter once as a 50-byte apply and three times as a 46-byte
	///         variant), <c>DispatchAddSpellResistance</c> <c>[0x1AB][0x64]</c>, and <c>Successful Knockback</c>
	///         (<c>0x10B1A6</c>) applied to the attacker as a self-marker - then the forced-move <c>MoveTask</c>, and
	///         the <c>SetPosIIR</c> pinning the destination ~1.7 s later. Only the immunity buff and the move pair are
	///         sent today; the rest are decoded but unwired, because <c>Recently Impaired</c> looks like a general
	///         impair diminishing-returns marker rather than a knockback record and wiring it here would be a guess
	///         about a system we have not decoded.
	///     </para>
	///     <para>
	///         <see cref="ResistanceDivisor" /> is an invention and is flagged as such. The stat
	///         <c>KnockbackResistance</c> (0x040D, LDB 1037) exists and is obviously meant to reduce this, but the one
	///         captured victim's value is unknown, so no scaling curve can be derived from the corpus at all. A linear
	///         falloff is the least surprising shape; it is here so the stat is read rather than ignored, and it
	///         should be replaced the moment a knockback is captured against two characters with different
	///         resistances. Until then, do not quote it as decoded.
	///     </para>
	/// </remarks>
	public static class Knockback
	{
		/// <summary>
		///     The spell retail applies to the victim at the instant of the knockback - the database names it
		///     "Knockback immunity". Applying it is capture-backed; what it guards (presumably chained knockbacks
		///     inside its window) is not, since the corpus has only one knockback to look at.
		/// </summary>
		public const uint ImmunitySpellId = 4784832;

		/// <summary>
		///     The measured displacement, in world units. Two independent knockbacks, from different attackers in
		///     different sessions, land on 3.01 and 3.000, so this is a constant.
		/// </summary>
		public const float DefaultDistance = 3.0f;

		/// <summary>
		///     [V0] Resistance at which the distance is halved. Invented - see the remarks on <see cref="Knockback" />.
		///     Chosen large enough that a character with no resistance stat is unaffected.
		/// </summary>
		public const float ResistanceDivisor = 1000f;

		/// <summary>
		///     Where <paramref name="target" /> ends up when knocked back by something at <paramref name="source" />.
		///     The push is horizontal - the Y of the result is the target's own, because the caller is expected to
		///     ground-snap it (a knockback that ignores terrain drops players through the floor or into a wall).
		/// </summary>
		/// <param name="resistance">The victim's <c>KnockbackResistance</c> at wire scale; 0 = none.</param>
		public static Vector3 Resolve(Vector3 source, Vector3 target, float distance = DefaultDistance, uint resistance = 0)
		{
			var scaled = distance * ResistanceScale(resistance);
			if (scaled <= 0f)
				return target;

			// Horizontal only: the Y component is deliberately dropped before normalising, so a knockback from an
			// attacker standing above or below does not launch the victim vertically.
			var away = new Vector3(target.X - source.X, 0f, target.Z - source.Z);
			var length = away.Length();

			// Degenerate: attacker and victim on the same spot. There is no "away" to push along, so nothing happens -
			// which is better than picking an arbitrary direction and teleporting the victim somewhere surprising.
			if (length < 1e-4f)
				return target;

			away /= length;
			return new Vector3(target.X + away.X * scaled, target.Y, target.Z + away.Z * scaled);
		}

        /// <summary>
        ///     The fraction of the nominal distance a victim with this resistance actually travels, in [0, 1].
        /// </summary>
        public static float ResistanceScale(uint resistance)
		{
			if (resistance == 0)
				return 1f;

			return Math.Clamp(ResistanceDivisor / (ResistanceDivisor + resistance), 0f, 1f);
		}
	}
}
