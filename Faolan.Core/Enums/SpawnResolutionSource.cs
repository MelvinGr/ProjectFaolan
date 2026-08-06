namespace Faolan.Core.Enums
{
	// How an NpcSpawnArea's creature (NpcTemplateId) was assigned - the provenance of the resolution, so a
	// machine-guessed area can be told apart from a human-verified one (#363). The RDB carries WHERE + how many, never
	// WHICH creature (that is server-authoritative), so a template is inferred from captures then confirmed by hand.
	public enum SpawnResolutionSource
	{
		// No creature assigned yet - the area's roam bounds + count are known but the template is not.
		None = 0,

		// The group NAME (or Name-category id, 4-char tag stripped) matched a spawnable NpcTemplate by name.
		// Deterministic / high confidence.
		Name = 1,

		// The MOST COMMON captured NpcSpawn inside the roam AABB (unanimous, >= 2 votes). Medium confidence; only
		// possible on maps we have captures for.
		Containment = 2,

		// Learned id -> creature from a Name/Containment resolution and propagated to a same-id group, gated by the
		// per-group name-disagreement guard (a group whose own name contradicts the id's creature is left unresolved).
		// Lower confidence - unverifiable on maps we have not captured.
		IdPropagation = 3,

		// Set by hand (a GM/editor pass), overriding any machine guess. Highest confidence.
		Manual = 4,

		// The group's own DECLARED creature race (RDB 1010506 `category=Race`, whose id is the 1000508 race id)
		// resolved to the single spawnable template we hold of that race. Only applied where the race is
		// UNAMBIGUOUS - one candidate template, so there is nothing to choose between - and where the group's name
		// does not contradict it. The client STATES the race, so this reads data rather than inferring from
		// geometry: a `GrasslandsRabbit` group declares race 72 = Rabbit and we hold exactly one rabbit. Where a
		// race has several templates the area is left None rather than one being picked (#518).
		RaceUnique = 5
	}
}
