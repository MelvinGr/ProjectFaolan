# Alternate Advancement / perks: where the client keeps it (Gitea #150)

The AA perk trees. Not implemented. The useful finding is a negative one about the wire and a positive one
about where the data lives — all of which is now imported, so what is left is rules rather than content.

⚠️ **They are not post-80 trees.** Every one of the twelve gates at levels **20–80**, so AA runs alongside
ordinary levelling from 20 up rather than starting where levelling stops.

## AA adds NO new messages

There is **not one** AA/perk method in the client's RPC catalog — 860 methods across 14 interfaces, and a sweep
for `perk` / `alternate` / `mastery` / `veteran` / `advancement` returns nothing
([`aoc-client-rpc-methods.json`](aoc-client-rpc-methods.json)). Nor does any of the 120 capture dumps carry one,
which follows. So AA is not a protocol gap: it rides the **spell** and **feat** channels that already exist, and
a perk is granted, trained and slotted the same way a spell or a feat is.

That is why this page is short. The work in #150 is content and rules, not decode.

## A perk is a SPELL with a PerkLine

The client asserts `"PerkLine is not 1, 2 or 3 on spell id %d"` (`ShortcutBars\PerkView.cpp`, `FUN_015679a0`)
when a drag lands on the perk bar: it takes the dragged `spell_id`, reads that spell's **PerkLine**, and expects
1, 2 or 3. The three values are the perk bar's three overlay textures and its three list views:

| PerkLine | client name | bar overlay |
|---|---|---|
| 1 | `PerkGeneral` | `bottombars/perkbar_slot_color_overlay_general.tga` |
| 2 | `PerkArchetype` | `…_archetype.tga` |
| 3 | `PerkClass` | `…_class.tga` |

`PerkLine` and `PerkSize` are **stat ids 1000013 / 1000014** (`aoc-client-stat-names.tsv`) — the client names
every stat id, and RDB property field ids ARE stat ids, so a perk would be an RDB spell record carrying field
1000013.

⚠️ **It does not, on this build.** `--report spell-fields` over the whole RDB spell set lists every property
field that occurs, and the largest is 1291 (`SpellLine2`); **1000013 and 1000014 never appear.** The property
run is only a small slice of a spell record, so this does not prove the data is absent from the file — but it
does mean `PerkLine` is not where the other spell properties are, and the client reads it through a runtime
accessor (`FUN_008d6c80`) rather than off the property run.

⚠️ **It is not needed to know a perk's line, either.** The line falls out of the feat-tree data — see the
sharing argument below — so the missing stat costs nothing but the name.

## AA lines are feat trees with ids >= 1,000,000

`FUN_01506a60` (log tag `GUI/AAFeatPlanner`, message `"No alternate advancement found for class %d"`) resolves
a class's AA line: it walks that class's feat-tree map and returns the first entry whose id is **> 999999** and
which maps to 1 in a second lookup. So the AA planner is the feat planner pointed at a reserved id range, and
"no alternate advancement found for class N" is what a class with no such tree produces.

⭐ **Which twelve trees those are is settled from the data, not from that predicate** — `TreeId`
1000177–1000190 (1000185 and 1000187 do not exist), one per playable class, and they separate from the other 36
on three independent axes at once: 240 rows each against 54–88, level gates of 20/80 against 10–30, and the only
rows RDB 1000099 prices. See [`client-data-tables.md`](../rdb/client-data-tables.md). The planner's second
lookup is therefore a route to the same twelve and not the thing standing in the way of anything.

⚠️ Note the trap: `Feats` already holds 4,169 rows with `Id >= 1,000,000` and they are **ordinary class feats**
(`Reave`, `Sever`, …). The client's test is on the **tree/line** id, not the feat id, so those rows are not
evidence either way.

## ⭐ The planner's whole API, and what it says the rules are

`FUN_01504bf0` registers `com.GameInterface.FeatWindows.AAFeatTree` — the AA window's entire contract with the
client, and therefore a list of exactly which questions a perk purchase has to answer:

| API | implementation | what it does |
|---|---|---|
| `GetAAFeatTreeDescription` / `MaxRows` / `MaxColumns` | `FUN_01506ee0`, `LAB_01506ba0`, `LAB_01506b90` | the tree grid — the `Tier` / `TreeColumn` columns of `FeatClassEntry` |
| `GetPointCost` | `FUN_01506700` | **the price**, read straight out of RDB 1000099 — see below |
| `GetAATokenCount` | `FUN_01505ff0` | the character's balance in one pool |
| `IsAAFeatTrainable` / `TrainAAFeat` / `HasFeat` | `FUN_015066a0` / `FUN_01506510` / `FUN_01506670` | the purchase, and it goes through the **ordinary `FeatHolder`** — which is why AA adds no messages. `TrainAAFeat` reads stats `0x130` / `0x12E` (the offline-training pair), then either starts the offline system (`FUN_01136730`, training mode `e_TrainUsingOfflineSystem`) or calls the FeatHolder's train virtual (`vtable+0xC`, `(featId, 1)`), which lands in `FeatHolder::TrainClassFeat` (`FUN_011386e0`, log channel `Feats/TrainClassFeat`): the same `FeatWrongLevel` / `FeatCategoryPointsMissing` / `FeatsNoMorePoints` / `FeatDependencyFailed` checks a class feat gets, then the id is queued for the `FeatIIR` state-`0x04` train request. ⚠️ So an AA purchase reaches a server as an ordinary feat train carrying a perk id — ours would charge it against the class feat-point budget (`FeatRules.WithinBudget`) rather than the token pool; what retail answers a token spend with is uncaptured (Gitea #150) |
| `GetAAXPCap` / `GetAANextGiveAwayCompletionPercentage` | `LAB_01506180` / `FUN_01506130` | the XP pool against `AlternateAdvancement_XPCap` |
| `SetTrainingMode` / `GetTrainingMode` / `IsOfflineTrainingAvailable` / `ResetOfflineTraining` | `FUN_01504bc0`, … | the OFFLINE training half (Gitea #143) |

It also registers two enums outright, which is where the numbering below comes from:

```
AAFeatPlannerTrainingMode:  e_TrainSpendingPoint, e_TrainUsingOfflineSystem
AAFeatPlannerPointType:     e_PvP = 2, e_PvE = 3, e_Generic = 4
```

⭐ **Those three values ARE `TokenPool` 2 / 3 / 4** (`AltAdvancementPvPToken` / `PvEToken` / `GeneralToken`, RDB
1000520). `GetPointCost` reads the AA cost table's middle word and switches on it into the GUI keys `PvPPoints`
/ `PvEPoints` / `GeneralPoints`, so a perk's price and the token pool it is paid from are one field. The decode
and its correction are in [`client-data-tables.md`](../rdb/client-data-tables.md).

## ⛔ The perk data is NOT missing — it is imported

This page used to end on "the work in #150 is content and rules, not decode", which is right, and on the
`PerkLine` stat being unreachable, which read as *the content is not here either*. It is:

* **the trees** — `FeatClassEntry`, `TreeId` 1000177–1000190, twelve trees of 240 rows, one per playable class,
  gated at levels 20–80. Their `PerkLine` is not a stored field but is recoverable from the data: a perk in all
  twelve trees is General (22 of them), one in the three classes of an archetype is Archetype (42), one in a
  single class is Class (201).
* **the prices** — `FeatPerkCost`, 3,096 rows off RDB 1000099, per placement and per pool.
* **the currency** — `DispatchUpdateTokenAmount` (0x204D, `UpdateTokenAmountCodec`), and the economy's constants
  in the tuning sheet (`AlternateAdvancement_XPCap` 1,000,000; 1 generic + 3 specific tokens per full pool;
  100 unspent per pool).

What is genuinely absent is the SERVER's half: what a kill pays into the AA pool, and the purchase being
exercised. Both need an endgame character, and neither is a decode.

## The rest of the surface, for whoever picks this up

* Veteran currency is three named stats — `VeteranMonths`, `VeteranCurrency`, `VeteranCurrencySpent`.
* `LearnedNewPerk` is the client-side notification hook; `AM_CATEGORY_PERKS` is its journal/alert category.
* `YouCanNotEquipPerksAtThisTime` / `YouCanNotUnequipPerksAtThisTime` are the client's own refusal strings, so
  the equip gate is client-side and driven by state we already send.
* The UI is `perk_window` / `PerkView` / `PerkList` / `PerkListView` / `SPPerkItem.xml`, and the bar is
  `Toggle_Perkbar` / `PerkbarActive` / `PerkBarSwitchButton`.

Open work tracked in Gitea #150.
