using System.Collections.Generic;

namespace Faolan.GameServer.Combat
{
	/// <summary>
	///     The termination guard for one walk of the <c>CastNano</c> chain - "this spell landed, so also run X on the
	///     same subject", where X may chain again. One instance is threaded through a single landing and its whole
	///     sub-tree; the next landing gets a fresh one.
	/// </summary>
	/// <remarks>
	///     <para>
	///         The guard is not defensive tidiness, because the shipped catalog needs it: over the 10,676
	///         <c>CastNano</c> rows the chain graph contains 56 cycles (<c>145503 -&gt; 4940266 -&gt; 4220203 -&gt;
	///         117806 -&gt; 145503</c> is one) and reaches a depth of 12, so an unguarded walk does not terminate on
	///         real data.
	///     </para>
	///     <para>
	///         Two bounds answer different questions. The visited set terminates the walk, applying a spell at most
	///         once per landing, which is also the right semantics: the same buff landing twice on one subject is a
	///         re-apply, and the buff wire treats that as a replace rather than a stack.
	///         <see cref="MaxApplications" /> bounds the fan-out - closure sizes are a median of 2 and a p90 of 10,
	///         but the largest is 78 (spell 3475257), and a single landing emitting 78 <c>ApplySpell</c> carriers
	///         would be a wire flood for one cast. Truncation is logged by the caller rather than silently accepted,
	///         because a chain that hits the cap is a question about the data rather than a normal outcome.
	///     </para>
	/// </remarks>
	public sealed class SpellChainWalk
	{
		// Bigger than every closure the corpus needs (p90 = 10) and far below the largest (78), so it only ever bites
		// on the pathological tail.
		public const int MaxApplications = 32;

		private readonly HashSet<uint> _visited = [];

		// The stacking lines this walk has already put a member of on the subject. A line is a mutual-exclusion group,
		// so a second member is not a second buff - see TryEnter.
		private readonly HashSet<uint> _lines = [];
		private bool _sealed;

		/// <summary>
		///     A walk that refuses every edge, for a carrier whose 53051 rows are a candidate registry rather than a
		///     chain - "these are the things that can hang off this event", with something else deciding which one
		///     actually does. <see cref="FatalityService" /> is the proven case and carries the evidence.
		/// </summary>
		/// <remarks>
		///     It is a walk rather than a flag on the caller because the refusal has to hold for the carrier's whole
		///     sub-tree: an edge suppressed at the top must not be reachable through a second edge further down.
		/// </remarks>
		public static SpellChainWalk Registry()
		{
			return new SpellChainWalk { _sealed = true };
		}

		/// <summary>
		///     Whether the cap stopped this walk short. The caller logs it naming the carrier, so the spell that hit it
		///     can be looked at rather than inferred.
		/// </summary>
		public bool Truncated { get; private set; }

		/// <summary>
		///     Claim <paramref name="spellBaseId" /> for this walk. False = already applied here (a cycle or a
		///     diamond), its stacking line is already spoken for, or the cap is spent. Callers seed it with the spell
		///     that started the walk, so a chain edge pointing back at the carrier is a no-op rather than an infinite
		///     re-apply.
		/// </summary>
		/// <remarks>
		///     One member per stacking line. A stacking line is a mutual-exclusion group, so a walk that applies two
		///     members of one line produces something the client cannot represent - ten ranks of
		///     <c>Furious Inspiration</c> (line 44235) on the bar at once, say.
		///     <para />
		///     Two shapes in the data collapse under this one rule, which is why it is one rule:
		///     <list type="bullet">
		///         <item>
		///             a rank ladder - each rank additionally chains the rank below it, so entering anywhere applies
		///             everything under it. 116 such edges across 62 carriers. Both ends are in the same line, so the
		///             second is refused; an edge landing in the carrier's own line cannot mean "also run this".
		///         </item>
		///         <item>
		///             a rank table - a nameless, duration-less line header carrying one edge per rank (105
		///             carriers). Its own line is 0, so the ladder reading alone does not touch it; this one does,
		///             because the ten targets share a line with each other.
		///         </item>
		///     </list>
		///     Which member wins is decided before this is called, and it is a decode rather than a pick: every edge of
		///     a rank table carries a criterion run gating it on
		///     <c>MustHavePassiveAbilityRunning &lt;the rank below&gt;</c>, the edges are stored highest-rank-first,
		///     and the top one ORs in itself, so <c>BuffService.ApplyChainedSpells</c> evaluates the run and the first
		///     edge that passes is a one-rank-per-proc upgrade. This class admits exactly one member per line. 14 of
		///     the 65 tables carry no gate at all, and there arrival order is the whole of it.
		/// </remarks>
		public bool TryEnter(uint spellBaseId, uint stackingLine = 0)
		{
			// A registry walk claims nothing, so `ApplySpell`'s own seeding call is a no-op too and every edge below
			// it is refused. The carrier itself is applied by the caller before the walk is consulted.
			if (_sealed)
				return false;

			if (!_visited.Add(spellBaseId))
				return false;

			// A line of 0 is "in no stacking line", which is most spells - it must never collide with another.
			if (stackingLine != 0 && !_lines.Add(stackingLine))
				return false;

			if (_visited.Count <= MaxApplications)
				return true;

			Truncated = true;
			return false;
		}
	}
}
