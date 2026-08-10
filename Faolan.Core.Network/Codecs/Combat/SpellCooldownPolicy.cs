using Faolan.Core.Database.Tables;

namespace Faolan.Core.Network.Codecs.Combat
{
	// WHEN retail sends a <see cref="LockTemplateCodec" /> and WHAT it puts in it (#768). Live-value policy, not a
	// codec - a neighbour like RemoteFormatSession, named for what it is.
	//
	// #768 recorded this as "retail sends a cooldown on 41-87% of casts and we send one on 100%", from a count of
	// LockTemplate bodies in a 45-packet window after each cast. That measurement conflates three things, and taking
	// them apart leaves a rule with no residue. Over all 687 captured casts, matching each cast to its own outcome
	// (the BccIIR phase pair, begin 0x04 -> complete 0x0A / abort 0x07) and its own spell (the Magic:Attack tail's
	// u64 low word IS the spell id):
	//
	//   * ABORTED casts do not lock, and we already never lock one - the cooldown is armed inside the completion
	//     branch (#271). That alone lifts the buff figure from 87% to 102/102 = 100%.
	//   * a spell with NO recast never locks, on either side.
	//   * the remaining misses are TWO families, below, and outside them retail locks every completed cast of a
	//     spell that has a recast: 100/101.
	//
	// So the cadence was never the difference. What IS different is the two exemptions and the VALUE.
	public static class SpellCooldownPolicy
	{
		// The shortest recast retail was ever seen to LOCK is 2.0 s, over eight distinct spells (Hellfire Breath,
		// Hellfire Stream, Mark of the Devourer, Siphon Unlife, Frozen Hatred, Cannibalism and both Dances). The one
		// spell below it - `Fires of Gehenna (Rank 1)`, recast 1.0 s - was cast 93 times in the corpus and locked
		// ZERO times. Nothing in the corpus sits between the two, so 1.0 s is where the boundary is drawn: a recast
		// no longer than the global cooldown is not worth greying a button for.
		//
		// ⭐ This also explains the observation #767 read as "the recast runs from the cast BEGIN". `Smite (Rank 1)`
		// is a 2.0 s cast with a 1.0 s recast whose button the client re-requested 2.43 s after the begin - which is
		// not evidence about when a timer starts at all, but about a button that was never greyed. It carries a 1.0 s
		// recast, so retail sent it no lock.
		public const uint ShortestLockedRecastCentiseconds = 200;

		// Whether a COMPLETED cast of this spell locks its action-bar button.
		//
		// ⚠️ The pet-point summons are the second exemption, and the discriminator is the POINT COST rather than
		// "is a summon". The necromancer's eleven `Undead Minion:` / `Deathless Acolyte:` / `Dread Archmage:` spells
		// all cost pet points, all state a 22.0 s recast, and across 28 captured casts retail sent no lock at all -
		// while `Ancient Soul`, which summons too but costs no points, locks on 7 of 7. A pet-point summon is gated
		// by the points, not by the bar.
		public static bool Locks(Spell spell)
		{
			if (spell == null || spell.RecastCentiseconds < ShortestLockedRecastCentiseconds)
				return false;

			return (spell.SummonPointCost ?? 0) == 0;
		}

		// What the body carries: the spell's WHOLE recast in seconds, not what is left of it.
		//
		// ⚠️ We used to subtract the elapsed cast time, on the reading that the recast runs from the begin. Retail
		// does not: 40 of the 43 spells the corpus locks carry exactly `RecastCentiseconds / 100`, cast time
		// included - `Flesh to Worms` is a 1.2 s cast with a 6.0 s recast and every one of its 70 locks reads 6.00,
		// and `Hellfire Breath` is a 2.8 s cast with a 2.0 s recast and every one of its 32 locks reads 2.00. That
		// last one is the case the subtraction got WRONG in both directions at once: it would have sent nothing at
		// all, because the recast had already elapsed by the time the cast finished. (The partial values the codec
		// documents are the RESTORE shape - time left on a lock a relog is re-stating - not the cast shape.)
		public static float LockSeconds(Spell spell)
		{
			return spell.RecastCentiseconds / 100f;
		}
	}
}
