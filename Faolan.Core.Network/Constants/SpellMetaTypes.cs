namespace Faolan.Core.Network.Constants
{
	// Spell METATYPE ids - the namespace `DispatchAddSpellResistance`'s first field and `NanoResistance`'s first slot
	// both index (#869). The catalog is the client's own: **LDB category 55200**, resolvable with
	// `--report ldb-strings --report-arg "id:#<n>"`, so a value here is a NAME we read rather than a guess.
	//
	// Only the two the corpus actually carries are named. The catalog has hundreds of entries and copying them in
	// would be inventing a registry: an id is worth a constant here when something sends or reads it.
	public static class SpellMetaTypes
	{
		// LDB 55200/427. Carried at 100% - knockback IMMUNITY. See Gitea #496 (knockback is not implemented; only
		// the KnockbackResistance stat exists, which is a different quantity from this).
		public const uint Knockback = 427;

		// LDB 55200/2005. Carried at 100%, always alongside the `Criminal Protection` buff (spell 3880938) and
		// removed in the same frame the buff ends: the team-mate exemption from the criminal system. See Gitea #503.
		public const uint CriminalTeam = 2005;

		// The only percentage the corpus carries, on both metatypes and in both directions. 100 = immune.
		public const uint FullImmunity = 100;
	}
}
