namespace Faolan.Core.Enums
{
	/// <summary>
	///     How an <c>NpcSpawnArea</c>'s creature (<c>NpcTemplateId</c>) was assigned - the provenance of the
	///     resolution, so a machine-guessed area can be told apart from a human-verified one.
	/// </summary>
	/// <remarks>
	///     The RDB carries where an area is and how many spawn in it, never which creature (that is
	///     server-authoritative), so a template is inferred from captures and then confirmed by hand.
	/// </remarks>
	public enum SpawnResolutionSource
	{
		/// <summary>No creature assigned yet - the area's roam bounds and count are known but the template is not.</summary>
		None = 0,

		/// <summary>
		///     The group name (or Name-category id, 4-char tag stripped) matched a spawnable <c>NpcTemplate</c> by
		///     name. Deterministic, high confidence.
		/// </summary>
		Name = 1,

		/// <summary>
		///     The most common captured <c>NpcSpawn</c> inside the roam AABB (unanimous, at least two votes). Medium
		///     confidence; only possible on maps we have captures for.
		/// </summary>
		Containment = 2,

		/// <summary>
		///     Learned id-to-creature from a Name or Containment resolution and propagated to a same-id group, gated
		///     by the per-group name-disagreement guard (a group whose own name contradicts the id's creature is left
		///     unresolved). Lower confidence - unverifiable on maps we have not captured.
		/// </summary>
		IdPropagation = 3,

		/// <summary>Set by hand (a GM or editor pass), overriding any machine guess. Highest confidence.</summary>
		Manual = 4,

		/// <summary>
		///     The group's own declared creature race (RDB 1010506 <c>category=Race</c>, whose id is the 1000508 race
		///     id) resolved to the single spawnable template we hold of that race.
		/// </summary>
		/// <remarks>
		///     Only applied where the race is unambiguous - one candidate template, so there is nothing to choose
		///     between - and where the group's name does not contradict it. The client states the race, so this reads
		///     data rather than inferring from geometry: a <c>GrasslandsRabbit</c> group declares race 72 = Rabbit and
		///     we hold exactly one rabbit. Where a race has several templates the area is left
		///     <see cref="None" /> rather than one being picked.
		/// </remarks>
		RaceUnique = 5
	}
}
