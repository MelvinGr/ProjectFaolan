using System.Collections.Generic;
using Faolan.Core.Enums;
using Faolan.Core.Rdb;
using Xunit;

namespace Faolan.Tests
{
	// The criteria stack machine (#790/#791). Every case here is a shape the client's own
	// `CriteriaItem_t::CheckCriteria` produces - the operator numbers are `CriteriaOperator`, which carries only names
	// the client states, so a test that passes here is a test against retail's vocabulary rather than ours.
	public class CriteriaEvaluatorTests
	{
		private static ItemTemplateStatModifiers.Criterion C(uint id, uint value, CriteriaOperator op)
		{
			return new ItemTemplateStatModifiers.Criterion(id, value, (uint)op);
		}

		// A stat lookup over a fixed sheet. ⚠️ Anything unlisted reads NULL - "this entity cannot supply that stat" -
		// which is a different answer from 0 and is what keeps an unresolvable comparison from dropping an effect.
		private static System.Func<CharacterStatId, int?> Sheet(params (CharacterStatId Stat, int Value)[] stats)
		{
			var map = new Dictionary<CharacterStatId, int>();
			foreach (var (stat, value) in stats)
				map[stat] = value;

			return s => map.TryGetValue(s, out var v) ? v : null;
		}

		// The overwhelmingly common case: an element with no conditions applies. 59,907 of the catalog's criteria
		// carry id 0, but the ELEMENTS carrying none at all are the bulk, and they must stay unconditional.
		[Fact]
		public void AnEmptyRun_IsUnconditional()
		{
			Assert.True(CriteriaEvaluator.Permits([], Sheet(), null, out var unevaluated));
			Assert.Equal(0, unevaluated);
		}

		[Theory]
		[InlineData(80, CriteriaOperator.Larger, 50u, true)]
		[InlineData(20, CriteriaOperator.Larger, 50u, false)]
		[InlineData(20, CriteriaOperator.Less, 50u, true)]
		[InlineData(50, CriteriaOperator.Equal, 50u, true)]
		[InlineData(50, CriteriaOperator.Unequal, 50u, false)]
		public void AComparison_ReadsTheSubjectsStat(int level, CriteriaOperator op, uint value, bool permits)
		{
			var criteria = new List<ItemTemplateStatModifiers.Criterion> { C((uint)CharacterStatId.Level, value, op) };

			Assert.Equal(permits, CriteriaEvaluator.Permits(criteria, Sheet((CharacterStatId.Level, level)), null, out _));
		}

		// ⭐ The client's own reading: a run with no logic operator is a CONJUNCTION - it logs "Implicitly ANDing all
		// criteria!" when the stack does not reduce to one entry.
		[Fact]
		public void LeftoversAreImplicitlyAnded()
		{
			var sheet = Sheet((CharacterStatId.Level, 80), (CharacterStatId.CombatMode, 1));
			var bothTrue = new List<ItemTemplateStatModifiers.Criterion>
			{
				C((uint)CharacterStatId.Level, 50, CriteriaOperator.Larger),
				C((uint)CharacterStatId.CombatMode, 1, CriteriaOperator.Equal)
			};
			var oneFalse = new List<ItemTemplateStatModifiers.Criterion>
			{
				C((uint)CharacterStatId.Level, 50, CriteriaOperator.Larger),
				C((uint)CharacterStatId.CombatMode, 0, CriteriaOperator.Equal)
			};

			Assert.True(CriteriaEvaluator.Permits(bothTrue, sheet, null, out _));
			Assert.False(CriteriaEvaluator.Permits(oneFalse, sheet, null, out _));
		}

		// The three logic operators pop what the comparisons before them pushed - postfix, which is what makes the run
		// an expression rather than a list.
		[Fact]
		public void OrAndNot_CombineWhatWasPushedBeforeThem()
		{
			var sheet = Sheet((CharacterStatId.Level, 10));
			var trueCmp = C((uint)CharacterStatId.Level, 5, CriteriaOperator.Larger);
			var falseCmp = C((uint)CharacterStatId.Level, 50, CriteriaOperator.Larger);
			var none = C(0, 0, CriteriaOperator.Or);

			Assert.True(CriteriaEvaluator.Permits([trueCmp, falseCmp, none], sheet, null, out _));
			Assert.False(CriteriaEvaluator.Permits([trueCmp, falseCmp, C(0, 0, CriteriaOperator.And)], sheet, null, out _));
			Assert.True(CriteriaEvaluator.Permits([falseCmp, C(0, 0, CriteriaOperator.Not)], sheet, null, out _));
		}

		// ⚠️ The client fails the whole run on an under-flowing expression ("Too few entries!") rather than inventing
		// an operand, and so does this.
		[Fact]
		public void AnUnderflowingLogicOperator_FailsTheRun()
		{
			Assert.False(CriteriaEvaluator.Permits([C(0, 0, CriteriaOperator.And)], Sheet(), null, out _));
			Assert.False(CriteriaEvaluator.Permits([C(0, 0, CriteriaOperator.Not)], Sheet(), null, out _));
		}

		// ⭐ `TargetStat` is the same comparison read off the OTHER entity - the one axis a caster/target pair is for.
		[Fact]
		public void TargetStat_ReadsTheTargetNotTheCaster()
		{
			var criteria = new List<ItemTemplateStatModifiers.Criterion> { C((uint)CharacterStatId.Level, 80, CriteriaOperator.TargetStat) };

			Assert.True(CriteriaEvaluator.Permits(criteria, Sheet((CharacterStatId.Level, 1)), Sheet((CharacterStatId.Level, 80)), out _));
			Assert.False(CriteriaEvaluator.Permits(criteria, Sheet((CharacterStatId.Level, 80)), Sheet((CharacterStatId.Level, 1)), out _));
		}

		// ⚠️ THE policy decision, pinned so it cannot drift silently: an operator we cannot evaluate lets the element
		// through and is COUNTED. Failing it closed would drop effects that are applied today - a regression wearing
		// the shape of a fix - and 36.6% of the catalog's criteria sightings are still unnamed operators.
		[Fact]
		public void AnUnknownOperator_PermitsAndIsCounted()
		{
			// 113 is a real operator the catalog uses 1,180 times and `CriteriaOperator` does not name.
			var criteria = new List<ItemTemplateStatModifiers.Criterion> { new(0, 0, 113) };

			Assert.True(CriteriaEvaluator.Permits(criteria, Sheet(), null, out var unevaluated));
			Assert.Equal(1, unevaluated);
		}

		// ⚠️ A SELECTOR is not a condition. It pushes nothing, so a run that is one selector plus one comparison must
		// answer the comparison - if the selector pushed a boolean the implicit AND would read one entry too many.
		// ⚠️ The companion to the unknown-operator policy: a comparison on a stat the subject cannot supply also
		// permits, and is counted. Reading it as 0 would make `Larger` fail and remove an effect that applies today.
		[Fact]
		public void AComparisonOnAnUnsuppliedStat_PermitsAndIsCounted()
		{
			var criteria = new List<ItemTemplateStatModifiers.Criterion> { C((uint)CharacterStatId.SoulFragments, 5, CriteriaOperator.Larger) };

			Assert.True(CriteriaEvaluator.Permits(criteria, Sheet((CharacterStatId.Level, 80)), null, out var unevaluated));
			Assert.Equal(1, unevaluated);
		}

		[Fact]
		public void ASubjectSelector_PushesNothing()
		{
			var sheet = Sheet((CharacterStatId.Level, 10));
			var criteria = new List<ItemTemplateStatModifiers.Criterion>
			{
				new(0, 0, 100), // the catalog's most common operator - a selector, 13,815 sightings, always [0][0]
				C((uint)CharacterStatId.Level, 50, CriteriaOperator.Larger)
			};

			Assert.False(CriteriaEvaluator.Permits(criteria, sheet, null, out var unevaluated));
			Assert.Equal(0, unevaluated); // a selector is handled, not "unknown"
		}
	}
}
