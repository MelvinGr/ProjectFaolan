namespace Faolan.Core.Network.Constants
{
	/// <summary>
	///     Spell metatype ids - the namespace <c>DispatchAddSpellResistance</c>'s first field and
	///     <c>NanoResistance</c>'s first slot both index.
	/// </summary>
	/// <remarks>
	///     The catalog is the client's own LDB category 55200, resolvable with
	///     <c>--report ldb-strings --report-arg "id:#&lt;n&gt;"</c>, so a value here is a name read from the client.
	///     Only the two the corpus carries are named: the catalog has hundreds of entries, and an id earns a constant
	///     here when something sends or reads it.
	/// </remarks>
	public static class SpellMetaTypes
	{
		// LDB 55200/427. Carried at 100%: knockback immunity, a different quantity from the KnockbackResistance stat.
		// Open work tracked in Gitea #496.
		public const uint Knockback = 427;

		// LDB 55200/2005. Carried at 100%, always alongside the `Criminal Protection` buff (spell 3880938) and
		// removed in the same frame the buff ends: the team-mate exemption from the criminal system.
		// Open work tracked in Gitea #503.
		public const uint CriminalTeam = 2005;

		// The only percentage the corpus carries, on both metatypes and in both directions. 100 = immune.
		public const uint FullImmunity = 100;
	}
}
