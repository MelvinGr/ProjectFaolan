using System;
using System.Collections.Generic;
using Faolan.Core.Enums;

namespace Faolan.Core.Rdb
{
	/// <summary>
	///     ⭐ Evaluates a GameAction element's CRITERIA — the `[id][value][operator]` run that says under what
	///     condition the element applies (Gitea #790/#791, <c>docs/re/criteria-grammar.md</c>).
	/// </summary>
	/// <remarks>
	///     <para>
	///         This is the client's own machine, not a re-invention: <c>CriteriaItem_t::CheckCriteria</c> walks the run
	///         in order against a BOOLEAN STACK, and there are three kinds of operator — a COMPARISON evaluates
	///         `subject.stat &lt;op&gt; value` and pushes the result, a LOGIC operator pops one or two and pushes the
	///         combination, and a SUBJECT SELECTOR re-points which entity the following comparisons read from and
	///         pushes nothing. Leftovers are ANDed, which the client states outright when it logs
	///         `"Implicitly ANDing all criteria!"`.
	///     </para>
	///     <para>
	///         ⚠️ **An operator this does not know evaluates to TRUE**, and that is a deliberate, stated choice rather
	///         than an oversight. `CriteriaOperator` names ~40 of the 201 the switch handles, covering 63.4% of the
	///         catalog's sightings; the rest are numbers. Treating an unevaluable condition as FALSE would silently
	///         DROP effects that are applied today, which is a regression dressed as a fix — treating it as TRUE
	///         reproduces exactly today's behaviour for those elements while making the named ones correct.
	///         <see cref="Unevaluated" /> counts them so the gap is visible rather than assumed away.
	///     </para>
	///     <para>
	///         ⚠️ Subject selectors are honored as far as SWITCHING, not as resolving: 18/19/26/100 re-point at the
	///         user and 116 at a dynel the subject names, and this only models the user/target pair it is given. A
	///         selector it cannot resolve leaves the subject unchanged, which is the same conservative direction.
	///     </para>
	/// </remarks>
	public static class CriteriaEvaluator
	{
		/// <summary>
		///     Whether the run permits the element to apply. An EMPTY run is unconditional — most elements carry none.
		/// </summary>
		/// <param name="criteria">The element's triples, in wire order.</param>
		/// <param name="caster">Reads a stat off the entity that applied the element. Never null.</param>
		/// <param name="target">Reads a stat off the entity it is applied to; falls back to <paramref name="caster" />.</param>
		/// <param name="unevaluated">How many operators this build could not evaluate, and therefore let through.</param>
		/// <param name="hasRunning">
		///     ⭐ Whether the SUBJECT currently has a given spell running — what
		///     <c>MustHavePassiveAbilityRunning</c> asks, and the one operator in the named set that is not a stat
		///     read (Gitea #861). Null means the caller cannot answer, and the operator then falls through to the
		///     unevaluated path exactly as an unnamed one does.
		/// </param>
		public static bool Permits(IReadOnlyList<ItemTemplateStatModifiers.Criterion> criteria, Func<CharacterStatId, int?> caster,
			Func<CharacterStatId, int?>? target, out int unevaluated, Func<uint, bool>? hasRunning = null)
		{
			unevaluated = 0;
			if (criteria.Count == 0)
				return true;

			target ??= caster;
			var subject = caster;
			var stack = new Stack<bool>();

			foreach (var c in criteria)
			{
				switch ((CriteriaOperator)c.Operator)
				{
					// ---- logic: pops, pushes, takes no operand ----
					case CriteriaOperator.And when stack.Count >= 2:
						stack.Push(stack.Pop() & stack.Pop());
						continue;
					case CriteriaOperator.Or when stack.Count >= 2:
						stack.Push(stack.Pop() | stack.Pop());
						continue;
					case CriteriaOperator.Not when stack.Count >= 1:
						stack.Push(!stack.Pop());
						continue;
					case CriteriaOperator.True:
						stack.Push(true);
						continue;
					case CriteriaOperator.False:
						stack.Push(false);
						continue;

					// ⚠️ The client logs "Too few entries!" and fails the whole run rather than guessing; an
					// under-flowing expression is malformed data, so mirror that instead of inventing an operand.
					case CriteriaOperator.And:
					case CriteriaOperator.Or:
					case CriteriaOperator.Not:
						return false;

					// ⭐ ---- the buff-state operator (#861) ----
					//
					// `MustHavePassiveAbilityRunning` names a SPELL in its `value` slot, not a stat value, and asks
					// whether the subject is carrying it. It is what a rank TABLE's edges are gated on - each edge
					// requires the rank BELOW it - so without this one case a ten-edge ladder reads as ten
					// unconditional alternatives and the walk picks whichever it reaches first.
					case CriteriaOperator.MustHavePassiveAbilityRunning when hasRunning != null:
						stack.Push(hasRunning(c.Value));
						continue;

					// ---- comparisons: `subject.stat <op> value` ----
					//
					// ⚠️ A comparison whose stat the subject CANNOT SUPPLY is unevaluated, not false. Reading an
					// absent stat as 0 would make `Larger` fail and silently DROP an effect that applies today - a
					// regression wearing the shape of a fix, and the same mistake as failing an unknown operator
					// closed. `TargetStat` is the one that reads the other entity.
					case CriteriaOperator.Equal:
					case CriteriaOperator.Unequal:
					case CriteriaOperator.Less:
					case CriteriaOperator.Larger:
					case CriteriaOperator.LargerSecondForm:
					case CriteriaOperator.BitAnd:
					case CriteriaOperator.BitOr:
					case CriteriaOperator.TargetStat:
					{
						var read = (CriteriaOperator)c.Operator == CriteriaOperator.TargetStat ? target : subject;
						if (read((CharacterStatId)c.Id) is not { } value)
						{
							unevaluated++;
							stack.Push(true);
							continue;
						}

						stack.Push((CriteriaOperator)c.Operator switch
						{
							CriteriaOperator.Equal or CriteriaOperator.TargetStat => value == (int)c.Value,
							CriteriaOperator.Unequal => value != (int)c.Value,
							CriteriaOperator.Less => value < (int)c.Value,
							CriteriaOperator.BitAnd => (value & (int)c.Value) != 0,
							CriteriaOperator.BitOr => (value | (int)c.Value) != 0,
							_ => value > (int)c.Value
						});
						continue;
					}

					default:
						// A selector re-points the subject and pushes nothing - it is not a condition, so it must not
						// contribute a stack entry.
						if (SubjectSelectors.Contains(c.Operator))
						{
							subject = caster;
							continue;
						}

						unevaluated++;
						stack.Push(true);
						continue;
				}
			}

			// ⭐ "Implicitly ANDing all criteria": whatever is left on the stack is the answer, conjoined.
			foreach (var value in stack)
			{
				if (!value)
					return false;
			}

			return true;
		}

		/// <summary>
		///     The operators that re-point which entity the following comparisons read from and push NOTHING
		///     (docs/re/criteria-operators.tsv). They are why half the catalog's criteria carry no operand.
		/// </summary>
		private static readonly HashSet<uint> SubjectSelectors = [18, 19, 26, 100, 116];
	}
}
