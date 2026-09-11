using Faolan.Core.Database.Tables;

namespace Faolan.Core.Network.Codecs.Combat
{
	/// <summary>
	///     When retail sends a <see cref="LockTemplateCodec" /> body and what it puts in it. Live-value policy rather than
	///     a codec - a neighbour in the same sense as RemoteFormatSession.
	/// </summary>
	/// <remarks>
	///     Measured over all 687 captured casts, matching each cast to its own outcome (the BccIIR phase pair: begin 0x04 to
	///     complete 0x0A or abort 0x07) and its own spell (the Magic:Attack tail's u64 low word is the spell id):
	///     <list type="bullet">
	///         <item>An aborted cast never locks, so the cooldown is armed inside the completion branch.</item>
	///         <item>A spell with no recast never locks.</item>
	///         <item>
	///             Outside the two exemptions below, retail locks every completed cast of a spell that has a recast: 100 of
	///             101.
	///         </item>
	///     </list>
	///     What distinguishes retail is therefore the two exemptions and the value sent, not the cadence.
	/// </remarks>
	public static class SpellCooldownPolicy
	{
		/// <summary>
		///     The shortest recast retail is ever seen to lock, 2.0 s, over eight distinct spells (Hellfire Breath, Hellfire
		///     Stream, Mark of the Devourer, Siphon Unlife, Frozen Hatred, Cannibalism and both Dances). The one spell below
		///     it - Fires of Gehenna (Rank 1), recast 1.0 s - was cast 93 times in the corpus and locked none of them, and
		///     nothing in the corpus sits between the two. A recast no longer than the global cooldown is not worth greying a
		///     button for.
		/// </summary>
		/// <remarks>
		///     This is also why an unlocked button can be re-requested before its recast has elapsed: Smite (Rank 1) is a
		///     2.0 s cast with a 1.0 s recast that the client re-requested 2.43 s after the begin, which says nothing about
		///     when a timer starts and everything about a button that was never greyed.
		/// </remarks>
		public const uint ShortestLockedRecastCentiseconds = 200;

		/// <summary>
		///     Whether a completed cast of this spell locks its action-bar button.
		/// </summary>
		/// <remarks>
		///     The second exemption is the pet-point summons, and the discriminator is the point cost rather than being a
		///     summon: the necromancer's eleven Undead Minion / Deathless Acolyte / Dread Archmage spells all cost pet points,
		///     all state a 22.0 s recast, and across 28 captured casts retail sent no lock at all, while Ancient Soul - also a
		///     summon, but costing no points - locks on 7 of 7. A pet-point summon is gated by the points, not by the bar.
		/// </remarks>
		public static bool Locks(Spell spell)
		{
			if (spell == null || spell.RecastCentiseconds < ShortestLockedRecastCentiseconds)
				return false;

			return (spell.SummonPointCost ?? 0) == 0;
		}

		/// <summary>
		///     What the body carries: the spell's whole recast in seconds, not what is left of it.
		/// </summary>
		/// <remarks>
		///     The elapsed cast time must not be subtracted. 40 of the 43 spells the corpus locks carry exactly
		///     RecastCentiseconds / 100, cast time included: Flesh to Worms is a 1.2 s cast with a 6.0 s recast and all 70 of
		///     its locks read 6.00, and Hellfire Breath is a 2.8 s cast with a 2.0 s recast and all 32 of its locks read 2.00
		///     - a subtraction there would send nothing at all, the recast having already elapsed when the cast finished. The
		///     partial values <see cref="LockTemplateCodec" /> documents are the restore shape, the time left on a lock a
		///     relog is re-stating, not the cast shape.
		/// </remarks>
		public static float LockSeconds(Spell spell)
		{
			return spell.RecastCentiseconds / 100f;
		}
	}
}
