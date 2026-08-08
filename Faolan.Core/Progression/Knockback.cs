using System;
using System.Numerics;

namespace Faolan.Core.Progression
{
	/// <summary>
	///     Knockback (#496): the server shoving an entity along a segment it did not ask to walk. Pure geometry +
	///     resistance, so it is testable without a world.
	/// </summary>
	/// <remarks>
	///     <para>
	///         The WIRE side is decoded and byte-exact (<c>MoveTaskCodec.BuildForcedMoveBody</c>, mode 0x05, with a
	///         <c>SetPosIIR</c> pinning the destination). What follows is the part that is NOT decoded, and the
	///         distinction matters when reading these constants.
	///     </para>
	///     <para>
	///         The corpus now contains <b>TWO</b> knockbacks, and the second one confirms both halves of what the
	///         first could only suggest. The original displaced the player <b>3.01 units</b> and came with an
	///         <c>ApplySpell</c> of spell <b>4784832</b>, which the database names <b>"Knockback immunity"</b> (10 ms)
	///         — the mechanic naming itself.
	///     </para>
	///     <para>
	///         ⭐ The second, an inbound knockback on the player from a <c>Wharf Rat Cutthroat</c>
	///         [V2: NecroL80_mage-necromancer_login_gameplay_annotated_3.tshark.json frame 7790, annotated "got
	///         knockedback"], measures <b>3.000</b> horizontal — and the attacker→victim distance goes
	///         <b>3.255 → 6.255</b>, i.e. exactly the displacement. So the two open questions are answered by an
	///         independent event: the distance is a <b>constant 3.0</b> rather than something scaled per ability or
	///         per level, and the push is <b>directly along the attacker→victim ray</b>, not along the attacker's
	///         facing (which a single head-on sample could not separate).
	///     </para>
	///     <para>
	///         The second knockback also shows the batch is bigger than the immunity buff. Retail sends, in one
	///         instant: <c>Knockback immunity</c> and <c>Recently Impaired</c> on the victim (the latter once as a
	///         50-byte apply and three times as a 46-byte variant), <c>DispatchAddSpellResistance</c>
	///         <c>[0x1AB][0x64]</c>, and <c>Successful Knockback</c> (<c>0x10B1A6</c>) applied to the <b>attacker</b>
	///         as a self-marker — then the forced-move <c>MoveTask</c>, and the <c>SetPosIIR</c> pinning the
	///         destination ~1.7 s later. Only the immunity buff and the move pair are sent today; the rest are
	///         decoded but unwired, because <c>Recently Impaired</c> looks like a general impair diminishing-returns
	///         marker rather than a knockback record and wiring it here would be a guess about a system we have not
	///         decoded.
	///     </para>
	///     <para>
	///         ⚠️ <b><see cref="ResistanceDivisor" /> is an outright invention and is flagged as such.</b> The stat
	///         <c>KnockbackResistance</c> (0x040D, LDB 1037) exists and is obviously meant to reduce this, but the one
	///         captured victim's value is unknown, so no scaling curve can be derived from the corpus at all. A linear
	///         falloff is the least surprising shape; it is here so the stat is READ rather than ignored, and it should
	///         be replaced the moment a knockback is captured against two characters with different resistances. Until
	///         then, do not quote it as decoded.
	///     </para>
	/// </remarks>
	public static class Knockback
	{
		/// <summary>
		///     The spell retail applies to the victim at the instant of the knockback - the DB names it
		///     "Knockback immunity". Applying it is capture-backed; what it GUARDS (presumably chained knockbacks
		///     inside its window) is not, since the corpus has only one knockback to look at.
		/// </summary>
		public const uint ImmunitySpellId = 4784832;

		/// <summary>
		///     The measured displacement, in world units. Two independent knockbacks, from different attackers in
		///     different sessions, land on 3.01 and 3.000 — so this is a CONSTANT, not the one sample it used to be.
		/// </summary>
		public const float DefaultDistance = 3.0f;

		/// <summary>
		///     [V0] Resistance at which the distance is halved. Invented - see the remarks. Chosen large enough that a
		///     character with no resistance stat (the overwhelmingly common case today) is unaffected.
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
