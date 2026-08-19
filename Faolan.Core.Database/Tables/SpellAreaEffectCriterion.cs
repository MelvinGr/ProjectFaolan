using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	/// <summary>
	///     ⭐ One CONDITION on a <see cref="SpellAreaEffect" /> — the `[id][value][operator]` triple the element
	///     carries, in wire order. The twin of <see cref="SpellStatEffectCriterion" />, for the OTHER half of the
	///     element region (Gitea #861).
	/// </summary>
	/// <remarks>
	///     ⭐⭐ <b>This is what a rank TABLE's selection was missing.</b> A spell in no stacking line carrying one
	///     <c>CastNano</c> edge per rank of one line looks like ten alternatives with nothing to choose between them,
	///     and #813 could only take the first. It is not: every edge carries
	///     <c>MustHavePassiveAbilityRunning &lt;the rank BELOW it&gt;</c>, the edges are stored highest-rank-first, and
	///     the top one additionally ORs in itself. Read in order, first match wins, that is a <b>one-rank-per-proc
	///     upgrade ladder</b> — a fresh caster gets rank 1, someone already carrying rank 5 gets rank 6, and someone at
	///     the cap refreshes rank 10.
	///     <para>
	///         So the condition run is not decoration on these rows, it IS the mechanic. Without it the chain applies
	///         whichever edge it reaches first, which for `Furious Inspiration` handed a rank-10 buff to a character
	///         who had never proc'd it.
	///     </para>
	///     <para>
	///         ⚠️ Stored VERBATIM, operators we cannot evaluate included, for the same reason its stat-effect twin is:
	///         dropping them would make the table look complete while deciding those conditions are absent.
	///     </para>
	/// </remarks>
	[PrimaryKey(nameof(SpellId), nameof(Sequence), nameof(Pos))]
	public class SpellAreaEffectCriterion
	{
		public uint SpellId { get; set; }

		/// <summary>The link's ordinal within its record — <see cref="SpellAreaEffect.Sequence" />.</summary>
		public uint Sequence { get; set; }

		[ForeignKey($"{nameof(SpellId)},{nameof(Sequence)}")]
		public virtual SpellAreaEffect Effect { get; set; } = null!;

		/// <summary>
		///     This criterion's ordinal within the element's run. The run is a postfix EXPRESSION, so the order is
		///     load-bearing rather than incidental — it is what makes the rank ladder read top-down.
		/// </summary>
		public uint Pos { get; set; }

		/// <summary>
		///     The condition's subject — a stat id (<c>CharacterStatId</c>), 0 on the operators that take no operand,
		///     which on these rows is nearly all of them.
		/// </summary>
		public uint CriterionId { get; set; }

		/// <summary>
		///     The operand. ⚠️ On <c>MustHavePassiveAbilityRunning</c> — the operator that carries the rank ladder —
		///     this is a SPELL id, not a stat value.
		/// </summary>
		public uint Value { get; set; }

		/// <summary>The comparison, logic or subject-selector operator — <c>CriteriaOperator</c> where it is named.</summary>
		public uint Operator { get; set; }
	}
}
