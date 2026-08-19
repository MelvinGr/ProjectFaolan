using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	/// <summary>
	///     ⭐ One CONDITION on a <see cref="SpellStatEffect" /> — the `[id][value][operator]` triple the element
	///     carries, in wire order (Gitea #790/#791).
	/// </summary>
	/// <remarks>
	///     <para>
	///         Without these rows a conditional effect is applied unconditionally, because the import dropped the
	///         run: 987 of the 1,117 elements that look like exact duplicates in the spell catalog differ ONLY here,
	///         so a bonus that retail grants "while wielding a two-hander" or "on a random roll" was granted always.
	///         <c>CriteriaEvaluator</c> is what reads them back.
	///     </para>
	///     <para>
	///         ⭐ Its AREA/CHAIN twin is <see cref="SpellAreaEffectCriterion" /> (#861), added once it turned out that
	///         a rank table's whole selection rule lives in the criteria on its <c>CastNano</c> edges. The parser
	///         produced both halves all along; only this one was persisted.
	///     </para>
	///     <para>
	///         ⚠️ Stored VERBATIM, including the operators nothing can evaluate yet. `CriteriaOperator` names ~40 of
	///         the 201 the client's switch handles; dropping the rest on import would make the table look complete
	///         while silently deciding those conditions are absent, which is the one reading the bytes do not support.
	///     </para>
	/// </remarks>
	[PrimaryKey(nameof(SpellId), nameof(Sequence), nameof(Pos))]
	public class SpellStatEffectCriterion
	{
		public uint SpellId { get; set; }

		/// <summary>The effect's ordinal within its record — <see cref="SpellStatEffect.Sequence" />.</summary>
		public uint Sequence { get; set; }

		[ForeignKey($"{nameof(SpellId)},{nameof(Sequence)}")]
		public virtual SpellStatEffect Effect { get; set; } = null!;

		/// <summary>This criterion's ordinal within the element's run. The run is a postfix EXPRESSION, so the order
		/// is load-bearing rather than incidental.</summary>
		public uint Pos { get; set; }

		/// <summary>The condition's subject — a stat id (<c>CharacterStatId</c>), 0 on the operators that take no
		/// operand.</summary>
		public uint CriterionId { get; set; }

		public uint Value { get; set; }

		/// <summary>The comparison, logic or subject-selector operator — <c>CriteriaOperator</c> where it is named.</summary>
		public uint Operator { get; set; }
	}
}
