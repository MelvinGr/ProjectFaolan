using System.Collections.Generic;

namespace Faolan.GameServer.Combat
{
	// The termination guard for ONE walk of the `CastNano` chain (#784): "this spell landed, so also run X on the same
	// subject", where X may chain again. One instance is threaded through a single landing and its whole sub-tree; the
	// next landing gets a fresh one.
	//
	// ⚠️ The guard is not defensive tidiness - the shipped catalog needs it. Over the 10,676 `CastNano` rows the chain
	// graph contains **56 cycles** (`145503 -> 4940266 -> 4220203 -> 117806 -> 145503` is one) and reaches a depth of
	// **12**, so an unguarded walk does not terminate on real data.
	//
	// Two bounds, and they answer different questions:
	//   * the VISITED set terminates the walk. A spell is applied at most once per landing, which is also the right
	//     semantics: the same buff landing twice on one subject is a re-apply, and `SendMagicBuffIir` treats that as a
	//     replace rather than a stack (#274).
	//   * `MaxApplications` bounds the FAN-OUT. Closure sizes are a median of 2 and a p90 of 10, but the largest is 78
	//     (spell 3475257) and a single landing that emitted 78 `ApplySpell` carriers would be a wire flood for one cast.
	//     Truncation is logged by the caller rather than silently accepted, because a chain that hits the cap is a
	//     question about the data, not a normal outcome.
	public sealed class SpellChainWalk
	{
		// Bigger than every closure the corpus needs (p90 = 10) and far below the largest (78), so it only ever bites
		// on the pathological tail.
		public const int MaxApplications = 32;

		private readonly HashSet<uint> _visited = [];
		private bool _sealed;

		// ⭐ A walk that refuses EVERY edge, for a carrier whose 53051 rows are a candidate REGISTRY rather than a
		// chain - "these are the things that can hang off this event", with something else deciding which one
		// actually does. `FatalityService` is the proven case and carries the evidence; see its header.
		//
		// It is a walk rather than a flag on the caller because the refusal has to hold for the carrier's whole
		// sub-tree: an edge suppressed at the top must not be reachable through a second edge further down.
		public static SpellChainWalk Registry()
		{
			return new SpellChainWalk { _sealed = true };
		}

		// Whether the cap stopped this walk short. The caller logs it naming the carrier, so the spell that hit it can
		// be looked at rather than inferred.
		public bool Truncated { get; private set; }

		// Claim `spellBaseId` for this walk. False = already applied here (a cycle or a diamond), or the cap is spent.
		// Callers seed it with the spell that STARTED the walk, so a chain edge pointing back at the carrier is a
		// no-op rather than an infinite re-apply.
		public bool TryEnter(uint spellBaseId)
		{
			// A registry walk claims nothing, so `ApplySpell`'s own seeding call is a no-op too and every edge below
			// it is refused. The carrier itself is applied by the caller before the walk is consulted.
			if (_sealed)
				return false;

			if (!_visited.Add(spellBaseId))
				return false;

			if (_visited.Count <= MaxApplications)
				return true;

			Truncated = true;
			return false;
		}
	}
}
