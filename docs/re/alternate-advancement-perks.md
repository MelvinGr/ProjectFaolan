# Alternate Advancement / perks: where the client keeps it (Gitea #150)

The post-80 perk trees. Not implemented. The useful finding is a negative one about the wire and a positive
one about where the data lives.

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

## AA lines are feat trees with ids >= 1,000,000

`FUN_01506a60` (log tag `GUI/AAFeatPlanner`, message `"No alternate advancement found for class %d"`) resolves
a class's AA line: it walks that class's feat-tree map and returns the first entry whose id is **> 999999** and
which maps to 1 in a second lookup. So the AA planner is the feat planner pointed at a reserved id range, and
"no alternate advancement found for class N" is what a class with no such tree produces.

⚠️ Note the trap: `Feats` already holds 4,169 rows with `Id >= 1,000,000` and they are **ordinary class feats**
(`Reave`, `Sever`, …). The client's test is on the **tree/line** id, not the feat id, so those rows are not
evidence either way.

## The rest of the surface, for whoever picks this up

* Veteran currency is three named stats — `VeteranMonths`, `VeteranCurrency`, `VeteranCurrencySpent`.
* `LearnedNewPerk` is the client-side notification hook; `AM_CATEGORY_PERKS` is its journal/alert category.
* `YouCanNotEquipPerksAtThisTime` / `YouCanNotUnequipPerksAtThisTime` are the client's own refusal strings, so
  the equip gate is client-side and driven by state we already send.
* The UI is `perk_window` / `PerkView` / `PerkList` / `PerkListView` / `SPPerkItem.xml`, and the bar is
  `Toggle_Perkbar` / `PerkbarActive` / `PerkBarSwitchButton`.

Open work tracked in Gitea #150.
