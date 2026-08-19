using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Rdb;
using Xunit;

namespace Faolan.Tests
{
	// ⭐⭐ #861: WHICH member of a rank TABLE the chain picks. The ticket recorded it as "a pick, not a decode" -
	// `SpellChainWalk` took the walk's arrival order, which for `Furious Inspiration` is seq 0, which is rank 10.
	//
	// It is a decode after all, and the answer was on the elements the whole time: every edge of a rank table carries
	// a CRITERION run, and the run is `MustHavePassiveAbilityRunning <the rank BELOW it>`. Read in the order the
	// record stores them - highest rank first - the table is a ONE-RANK-PER-PROC UPGRADE LADDER. The importer had
	// simply never carried the criteria on these rows (`SpellAreaEffectCriterion` is what fixed that).
	//
	// The shape below is `Furious Inspiration`'s table (299074), verbatim from the shipped rows:
	//
	//     seq 0 -> 299064 (rank 10)  if [selector] Running(299064) [selector] Running(299073) Or
	//     seq 1 -> 299073 (rank  9)  if [selector] Running(299072)
	//     ...
	//     seq 8 -> 299066 (rank  2)  if [selector] Running(299065)
	//     seq 9 -> 299065 (rank  1)  if [selector] <operator 102, unnamed>
	//
	// ⭐ Note what the TOP edge does that no other does: it ORs in ITSELF, so a character already at rank 10 refreshes
	// rather than falling off the ladder. That asymmetry is the strongest single piece of evidence that this is an
	// upgrade ladder and not ten alternatives - there is nothing for it to mean otherwise.
	public class RankTableLadderTests
	{
		private const uint Rank10 = 299064, Rank9 = 299073, Rank8 = 299072, Rank7 = 299071, Rank6 = 299070;
		private const uint Rank5 = 299069, Rank4 = 299068, Rank3 = 299067, Rank2 = 299066, Rank1 = 299065;

		// The subject selector - operator 18, "dynamic-casts the target (else the user) and stores it as the subject".
		// It pushes NOTHING, which is why an evaluator that treated it as a condition would get every run below wrong.
		private const uint Selector = 18;
		private const uint Running = (uint)CriteriaOperator.MustHavePassiveAbilityRunning;
		private const uint Or = (uint)CriteriaOperator.Or;

		// Operator 102 - one of the unnamed `CriteriaYou`/`CriteriaTarget` family. Rank 1's gate, and the evaluator
		// lets an operator it cannot read through, which is what makes rank 1 the base case.
		private const uint UnnamedGate = 102;

		private static ItemTemplateStatModifiers.Criterion C(uint value, uint op)
		{
			return new ItemTemplateStatModifiers.Criterion(0, value, op);
		}

		// The table exactly as it ships, seq 0 first.
		private static readonly (uint Target, ItemTemplateStatModifiers.Criterion[] Run)[] Table =
		[
			(Rank10, [C(0, Selector), C(Rank10, Running), C(0, Selector), C(Rank9, Running), C(0, Or)]),
			(Rank9, [C(0, Selector), C(Rank8, Running)]),
			(Rank8, [C(0, Selector), C(Rank7, Running)]),
			(Rank7, [C(0, Selector), C(Rank6, Running)]),
			(Rank6, [C(0, Selector), C(Rank5, Running)]),
			(Rank5, [C(0, Selector), C(Rank4, Running)]),
			(Rank4, [C(0, Selector), C(Rank3, Running)]),
			(Rank3, [C(0, Selector), C(Rank2, Running)]),
			(Rank2, [C(0, Selector), C(Rank1, Running)]),
			(Rank1, [C(0, Selector), C(0, UnnamedGate)])
		];

		// The chain walk's rule, reduced to what this test is about: the first edge whose run permits it wins, and the
		// stacking line then refuses the rest. Mirrors `BuffService.ApplyChainedSpells`.
		private static uint? FirstApplied(IReadOnlySet<uint> running)
		{
			foreach (var (target, run) in Table)
			{
				if (CriteriaEvaluator.Permits(run, _ => null, null, out _, running.Contains))
					return target;
			}

			return null;
		}

		// A character who has never proc'd it gets RANK 1 - not rank 10, which is what arrival order gave.
		[Fact]
		public void AFreshSubject_GetsRankOne()
		{
			Assert.Equal(Rank1, FirstApplied(new HashSet<uint>()));
		}

		// ...and every rung after that is exactly one step up.
		[Theory]
		[InlineData(Rank1, Rank2)]
		[InlineData(Rank2, Rank3)]
		[InlineData(Rank3, Rank4)]
		[InlineData(Rank4, Rank5)]
		[InlineData(Rank5, Rank6)]
		[InlineData(Rank6, Rank7)]
		[InlineData(Rank7, Rank8)]
		[InlineData(Rank8, Rank9)]
		[InlineData(Rank9, Rank10)]
		public void EachProc_UpgradesByExactlyOneRank(uint held, uint expected)
		{
			Assert.Equal(expected, FirstApplied(new HashSet<uint> { held }));
		}

		// ⭐ At the cap it REFRESHES rather than falling through to rank 1 - the top edge's `Or ITSELF` is the only
		// thing in the table that could produce that, and it does.
		[Fact]
		public void AtTheCap_ItRefreshesTheTopRank()
		{
			Assert.Equal(Rank10, FirstApplied(new HashSet<uint> { Rank10 }));
		}

		// ⛔ The incumbent, stated so the change is legible: arrival order picks seq 0, which hands a rank-10 buff to
		// someone who has never proc'd the ability once.
		[Fact]
		public void ArrivalOrder_WouldHaveHandedOutTheTopRank()
		{
			Assert.Equal(Rank10, Table[0].Target);
			Assert.NotEqual(Table[0].Target, FirstApplied(new HashSet<uint>()));
		}

		// ⚠️ The subject selectors push NOTHING. If one contributed a stack entry the implicit AND at the end would
		// conjoin it, and every run above would evaluate against a phantom operand - so this is the property the
		// whole ladder rests on, gated separately from the ladder itself.
		[Fact]
		public void ASubjectSelector_ContributesNoStackEntry()
		{
			// Selector alone: nothing on the stack, nothing to conjoin, so the element is unconditional.
			Assert.True(CriteriaEvaluator.Permits([C(0, Selector)], _ => null, null, out _, _ => false));

			// Selector plus a FALSE condition must still be false - the selector must not have pushed a `true` that
			// an Or could have swallowed.
			Assert.False(CriteriaEvaluator.Permits([C(0, Selector), C(Rank1, Running)], _ => null, null, out _, _ => false));
		}

		// Without a buff-state predicate the operator is unevaluated and let through, which is the pre-#861 behaviour:
		// every edge permitted, so arrival order decides. Stated as a test because it is the exact regression to
		// notice if the predicate ever stops being threaded through.
		[Fact]
		public void WithoutTheBuffPredicate_EveryEdgeIsPermitted()
		{
			var permitted = Table.Count(t => CriteriaEvaluator.Permits(t.Run, _ => null, null, out _));

			Assert.Equal(Table.Length, permitted);
		}
	}
}
