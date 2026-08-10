# GameAction CRITERIA — a postfix expression the client evaluates over a boolean stack

Every GameAction element carries a run of `[id][value][operator]` triples — 74,593 of them across the spell
catalog — and they are the **conditions under which the element applies**. The reader was recovered with #785
(`FUN_006e9f20`, which validates the triple and names it in its own assert, `"invalid criterion id/value/operator ("`).
This page is the EVALUATOR, recovered for Gitea #790.

## Where it lives

- the criteria are a `fcVector` of 12-byte records at **`GameActionData + 0x50`** — the element reader
  `FUN_00899a60` reserves it (`ADD ECX,0x50; CALL <reserve>`) and pushes each triple into it;
- **`FUN_006dd970` is `CriteriaItem_t::CheckCriteria`** — it says so itself, in
  `"CriteriaItem_t::CheckCriteria[%d:%u], Criteria got e_Operator_Illegal!"`. It takes the vector as its first
  argument and returns a bool;
- `FUN_0089b9c0` is the caller that gates an element on it: it passes `element + 0x50` and returns 0 or 1.

## ⭐ It is a stack machine, and the run is a postfix expression

`CheckCriteria` walks the vector in order (`EAX += 0xc` per step) against a **boolean stack** — a byte array
with its depth in one local. Three kinds of operator:

| kind | what it does with the stack | operand |
|---|---|---|
| **comparison** | evaluates `subject.stat <op> value` and PUSHES the result | `id` = a stat id, `value` = the number |
| **logic** | POPS one or two and pushes the combination | none — `id` and `value` are both 0 |
| **subject selector** | re-points the subject subsequent criteria read from; pushes NOTHING | none |

⚠️ **Leftovers are implicitly ANDed.** After the loop the client logs
`"CriteriaItem_t::CheckCriteria[…], Implicitly ANDing all criteria! state = "` when the stack is not down to one
entry — so a run with no logic operator at all is a conjunction of its comparisons, which is why so many records
carry a bare list of them.

⭐ **This is what explains the `[0][0][op]` half of the corpus** — 37,540 of the 74,593 criteria carry no
operand at all. They are not a degenerate comparison; they are the logic and selector operators, which have
nothing to compare. The two families split cleanly:

```
logic     : 3 Or (12,802 sightings as [0][0]) · 4 And · 42 Not · 98 True · 99 False
selectors : 18 · 19 · 26 · 100 · 116     (13,815 sightings for 100 alone)
```

The three logic operators guard their own underflow, and the guards name them outright:
`" ERROR: Criteria failed on Operator_Or: Too few entries! Template : "`, and the same for `Operator_And` and
`Operator_Not`.

## ⭐ A criterion's `id` IS a stat id

Every one of the 42 distinct ids resolves in the client's own stat-name table (`CharacterStatId`,
[`aoc-client-stat-names.tsv`](aoc-client-stat-names.tsv)), and — more to the point — resolves to something a
condition would plausibly test:

| id | client name | criteria | id | client name | criteria |
|---:|---|---:|---:|---|---:|
| 0 | `Flags` | 59,907 | 194 | `InPlay` | 209 |
| 150 | `CastingLevel` | 5,992 | 67 | `SpecializedClass` | 142 |
| 968 | `ComboAttackNumber` | 2,959 | 31 | `Archetype` | 118 |
| 965 | `ApprenticeLevel` | 1,892 | 20 | `CombatMode` | 87 |
| 54 | `Level` | 821 | 59 | `Sex` | 79 |
| 589 | `CurrentPlayfield` | 475 | 27 | `HealthCurrent` | 70 |
| 1037 | `KnockbackResistance` | 432 | 144 | `SoulFragments` | 68 |
| 520 | `Rnd` | 425 | 89 | `Race` | 24 |
| 522 | `LastRnd` | 354 | 121 | `SuperFamily` | 21 |
| 525 | `PercentRemainingHealth` | 239 | 1097 | `MurderPoints` | 12 |

⚠️ **The hit RATE is not the evidence** — `--report spell-element-lists` prints the 50 OPERATOR values through
the same table as a control and **all 50 of those "resolve" too**, because the table is dense at low ids. What
distinguishes them is coherence: the ids read as a condition vocabulary end to end, while the operators come
back as `RespecPoints`, `Breed`, `NpcTitle`, `VolumeMass`, `CATMesh` — noise.

⭐ And the client corroborates two of them directly: operator `0` (equal) refuses with **`WrongPlayfield`**, and
`589` is `CurrentPlayfield`; `Rnd` / `LastRnd` at 425 + 354 sightings is a **random roll**, i.e. a proc chance
stated as a condition.

## The operator vocabulary

The switch is `CMP ESI,0x3f7 / CMP ESI,0xc8 / MOVZX EDX,byte [ESI + 0x6e67d0] / JMP [EDX*4 + 0x6e6630]` — a
201-value dense switch over 104 branches, plus a special case for `0x3F7` and a default that logs
`e_Operator_Illegal`. [`criteria-operators.tsv`](criteria-operators.tsv) is the whole table, value by value;
`CriteriaOperator` carries the ~40 the client NAMES. Highlights:

| op | name | op | name |
|---:|---|---:|---|
| 0 / 1 / 2 / 24 | `Equal` / `Less` / `Larger` / `Unequal` | 31–34 | `Has(Not)WornItem` / `Has(Not)WieldedItem` |
| 22 / 23 | `BitAnd` / `BitOr` | 35 / 36 | `HasFormula` / `HasNotFormula` (a formula is a SPELL) |
| 3 / 4 / 42 | `Or` / `And` / `Not` | 38 / 39 / 40 | `IsValid` / `IsInvalid` / `IsAlive` |
| 5 / 6 | `Time_Less` / `Time_Larger` | 67 / 68 | `NumberOfItems` / `PrimaryTemplate` |
| 9 / 12 | `ID` / `TargetStat` | 91 | `MustHavePassiveAbilityRunning` |
| 15 / 55 / 56 | `Area` | 93 / 103 | `HasFeat` / `HasNotFeat` |
| 25 | `GC_HasQuestJournalSpace` | 98 / 99 | `True` / `False` |
| 27–30 | `OnValidTarget` / `OnValidUser` | 118 / 119 | `MustBeInCombat` / `MustNotBeInCombat` |

⚠️ **About 60 branches carry no string of their own and are left unnamed** — including the `92 / 94 / 95 / 96 /
97 / 101 / 102` cluster, which is clearly one family (every branch of it builds a refusal line prefixed
`CriteriaTarget` or `CriteriaYou`) but whose members are told apart only by which container helper they call.
They are numbers in the TSV rather than invented names.

⭐ ⚠️ **Four observed operators are values the client REJECTS** — `7`, `8`, `44` and `84` fall to the
`e_Operator_Illegal` default, and each occurs exactly once in the whole catalog. A single sighting of a value
the client refuses to evaluate is a mis-split element, not a rare operator, so they are a decode-quality signal
rather than vocabulary.

## What this unblocks

Two elements that state the same effect under different conditions are indistinguishable to every consumer
today — 987 of the 1,117 apparent duplicates in the spell catalog differ ONLY in their criteria (#785). With the
operator vocabulary in hand a consumer can finally ask "does this element apply?", which is what a bonus that
should only be granted while wielding a two-hander, or only above a level, or only on a random roll, needs.

## How it was recovered

- `tools/ghidra/FindOffInRange.java` — who else reaches `GameActionData + 0x50`, scoped to the module (the
  offset has thousands of hits image-wide and a handful inside it);
- `tools/ghidra/DumpSwitchTable.java` — value → branch, by composing the two switch tables;
- `tools/ghidra/DumpSwitchBranchStrings.java` — branch → the strings it logs, which is where the names are.

Refs: #790, #785, [`gameactionlist-grammar.md`](gameactionlist-grammar.md),
`Faolan.Core.Rdb/ItemTemplateStatModifiers.cs` (`Criterion`).
