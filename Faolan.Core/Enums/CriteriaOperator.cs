namespace Faolan.Core.Enums
{
	/// <summary>
	///     The operator of a GameAction element's criterion - the third word of the <c>[id][value][operator]</c>
	///     triple <c>CriteriaItem_t::CheckCriteria</c> evaluates (<c>docs/re/criteria-grammar.md</c>).
	/// </summary>
	/// <remarks>
	///     <para>
	///         Every member here is a name the client states, taken from that function's own log and assert
	///         text (<c>" Criteria failed on Operator_HasWornItem: "</c>, <c>"Criteria Operator_True"</c>,
	///         <c>"Criteria e_Operator_GC_HasQuestJournalSpace failed because pcQuestHolder == NULL!"</c>) or read
	///         directly out of the branch's code where it is unambiguous - the three logic operators pop and push a
	///         boolean stack, which is what they are. Nothing is inferred from what a capture happened to contain.
	///     </para>
	///     <para>
	///         It is deliberately partial. The switch has 201 values over 104 branches and about 40 are named;
	///         the rest have no string of their own and are left as numbers rather than guessed at. So a decoder must
	///         treat an unnamed value as data, not as an error - <c>Enum.IsDefined</c> is the check, and
	///         <c>docs/re/criteria-operators.tsv</c> is the full value -> branch table including the unnamed ones.
	///     </para>
	///     <para>
	///         Several branches are reached by two values and their log text names the branch rather than either
	///         value - `OnValidTarget` (27 and 28), `OnValidUser` (29 and 30), `Area` (15, 55 and 56). The pair is
	///         almost certainly the predicate and its negation, but nothing establishes which, so they stay out of
	///         here and live in the TSV with that caveat; naming one of them would be a coin flip wearing a name.
	///     </para>
	/// </remarks>
	public enum CriteriaOperator : uint
	{
		// ---- Comparisons. The criterion's `id` is a stat id and `value` the number it is compared against. ----

		/// <summary>`stat == value`. Its branch carries the specific refusal messages - `WrongPlayfield` when the
		/// stat is <c>CurrentPlayfield</c>, `Criteria_TargetPolymorphed`, `Criteria_TargetInForm`.</summary>
		Equal = 0,

		/// <summary>`stat &lt; value`; refuses with `YouAlreadyHaveALargerBagEquipped` on the bag stat.</summary>
		Less = 1,

		/// <summary>`stat &gt; value`; refuses with `Criteria_IsRequiredToBeAtLeast` / `YouCanNotUse`.</summary>
		Larger = 2,

		/// <summary>`stat != value`.</summary>
		Unequal = 24,

		/// <summary>A second `larger` branch, logging `" larger success, "` with the stat in parentheses.</summary>
		LargerSecondForm = 83,

		/// <summary>Bitwise `stat &amp; value`, logged as `" &amp; success "` / `" &amp; failed "`.</summary>
		BitAnd = 22,

		/// <summary>Bitwise `stat | value`, logged as `" | success "` / `" | failed "`.</summary>
		BitOr = 23,

		// ---- Logic. These take no operand - id and value are both 0 - and work a boolean stack. ----

		/// <summary>Pops two, pushes their OR. Under-flowing it logs `Criteria failed on Operator_Or: Too few
		/// entries!`.</summary>
		Or = 3,

		/// <summary>Pops two, pushes their AND (`Operator_And`).</summary>
		And = 4,

		/// <summary>Pops one, pushes its negation - `"Criteria Operator_Not: !"`.</summary>
		Not = 42,

		/// <summary>Pushes true - `"Criteria Operator_True"`.</summary>
		True = 98,

		/// <summary>Pushes false - `"Criteria Operator_False"`.</summary>
		False = 99,

		// ---- Time. ----

		/// <summary>`Operator_Time_Less`.</summary>
		TimeLess = 5,

		/// <summary>`Operator_Time_Larger`.</summary>
		TimeLarger = 6,

		// ---- Items the subject is wearing / wielding, and the formulas (spells) it knows. ----

		/// <summary>`Operator_HasWornItem`.</summary>
		HasWornItem = 31,

		/// <summary>`Operator_HasNotWornItem`.</summary>
		HasNotWornItem = 32,

		/// <summary>`Operator_HasWieldedItem`.</summary>
		HasWieldedItem = 33,

		/// <summary>`Operator_HasNotWieldedItem`.</summary>
		HasNotWieldedItem = 34,

		/// <summary>`Operator_HasFormula` - a formula is a spell the subject knows.</summary>
		HasFormula = 35,

		/// <summary>`Operator_HasNotFormula`; refuses with `YouAlreadyHaveThisSpell`.</summary>
		HasNotFormula = 36,

		/// <summary>`Operator_NumberOfItems`.</summary>
		NumberOfItems = 67,

		/// <summary>`Operator_PrimaryTemplate`.</summary>
		PrimaryTemplate = 68,

		/// <summary>`Operator_ID`.</summary>
		Id = 9,

		// ---- The subject's validity and state. ----

		/// <summary>`Criteria: IsValid:`.</summary>
		IsValid = 38,

		/// <summary>`Criteria: IsInvalid:`.</summary>
		IsInvalid = 39,

		/// <summary>`Criteria: IsAlive(`.</summary>
		IsAlive = 40,

		/// <summary>`Operator_TargetStat` - the same comparison as <see cref="Equal" /> and friends but read off the
		/// target rather than the current subject.</summary>
		TargetStat = 12,

		/// <summary>`Criteria e_Operator_GC_HasQuestJournalSpace`, which also carries `QuestLimitReached`.</summary>
		GcHasQuestJournalSpace = 25,

		/// <summary>`MustBeInCombatToUseThisItem`.</summary>
		MustBeInCombat = 118,

		/// <summary>`MustNotBeInCombatToUseThisItem`.</summary>
		MustNotBeInCombat = 119,

		/// <summary>`MustHavePassiveAbilityRunning`.</summary>
		MustHavePassiveAbilityRunning = 91,

		/// <summary>The feat check - refuses with `CriteriaMissingFeat` / `YouCanNotUseYet` / `YouCanNotUse`.</summary>
		HasFeat = 93,

		/// <summary>`CriteriaHasNotFeatFailed`.</summary>
		HasNotFeat = 103,
	}
}
