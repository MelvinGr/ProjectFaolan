# RDB item-template stat modifiers (#3)

Decode progress on the per-item stat bonuses needed for server-side MAX-STAT aggregation (#3 — so a
character's MaxHealth/attributes reflect worn gear and the regen bar can fill past the class base).

**Source.** The bonuses are NOT in our `Item` table or the old C++ `p_items` (both only carry name + the
Data quad). They live in the **encrypted RDB item-template**, which we already TEA-decrypt (key = the item's
`Key1..4` quad, stored as `Item.Key`; `RdbItemImporter`). Dump the decrypted templates with:

```
dotnet run --project Faolan.Importer -- --only rdb-items --rdb <RDB dir> --captures <data-repo>/Captures --dump-templates <dir>
```

Each file `<type>_<id>.bin` = `[12-byte RDB-Id header][body]`. Dissector: `DispatchAddItemTests`.

## Body grammar

After a short fixed header the body is an RDB **property table**: a run of `[fieldId u32][value u32]` pairs
(LE) with **strictly DECREASING fieldId**. Field ids are RDB template field numbers (973, 972, 971, 857,
615, 499, 496, 462, 460, 436, 360, 316, 294, 287, 286, 285, 211, 148, …), NOT wire stat ids.

### VALIDATED — the primary-attribute modifier

| field | meaning | values seen |
|-------|---------|-------------|
| `972` | modifier's **wire stat id** | only `804`=Str / `806`=Con / `808`=Int / `810`=Wis / `814`=Dex (`0x324/0x326/0x328/0x32a/0x32e`, per `Archive` `devStuff/StatIds.txt`) |
| `971` | amount | `+1` / `+2` / `+3` |

Self-validation: across all 1964 decrypted templates, field 972 holds an attribute stat id in **253** items
and a non-attribute value in **0** — so this slot is safe to import as a `(statId, amount)` attribute bonus.
In practice only Str (178) and Dex (75) appear as the *primary* attribute.

## VALIDATED — the full stat-modifier list (the `[2][9]` record region)

The rich stat list (the whole tooltip: attributes, vitals AND ratings) is a run of fixed **10-word records**
after the property table, NOT in the strictly-decreasing property run:

```
[typeMarker u32][0][4][0][1][0][2][9][statId u32][amount u32]
                        └──────┬──────┘
             the 6-word signature 4,0,1,0,2,9 anchors each record
```

- `statId` = the **wire stat id** (same id space as the `0x96B8DC59` vitals / snapshot stat block).
- `amount` = the bonus **at wire scale** — divide by the stat's `CharacterStatId.GetScale()` to get the
  displayed value: **÷100 for vitals** (HealthMax `0x0001`, StaminaMax `0x1f9`, ManaMax `0x1fb`) and
  MagicDamage (`0x35d`), **÷10 for attributes** (Str/Con/Int/Wis/Dex), **÷1 for ratings**.

**Anchor on the 6-word signature, not a raw `[2][9]` scan** — the raw scan yields 2508 hits (958 of them
coincidental `2,9` word pairs); the signature yields 1550 clean records (the word before the `4` is `0` in
100% of them). Even so a handful of signature hits carry an RDB **resource type id** as `statId`
(`1000017`/`1000018`) or garbage (`0x3de071`, `0xd00d`) — filter by a **known-stat-id whitelist**, exactly as
field 972 is whitelisted to the 5 attribute ids.

### Scale/divisibility proof + in-client ground truth

Across all 3201 decrypted templates every vital `amount` is divisible by 100 and every attribute `amount` by
10 — **0 violations in 448 records** (`gcd` per stat id = its scale). And it reproduces an
**independently in-client-verified tooltip** (`docs/re/spawn-dissection-plan.md` STEP (b), 2026-06-07): the
**Royal Signet** (template `3774542`) shows *+30 Constitution / +52 Magic Damage / +15 Hit Rating* in-client,
and its three records decode **exactly**:

| record | ÷ scale | tooltip |
|--------|---------|---------|
| Con `0x326` = 300 | ÷10 | **+30 Constitution** |
| `0x35d` = 5200 | ÷100 | **+52 Magic Damage** (⇒ `0x35d` = Magic Damage, ×100) |
| `0x136` = 15 | ÷1 | **+15 Hit Rating** (⇒ `0x136` = Hit Rating, ×1) |

So the `[2][9]` region is the authoritative modifier list; the earlier "amounts too large for attribute
points" read was just the missing ÷10 scale. Con/Int/Wis appear only here; **Str/Dex appear here too** (the
big values), largely disjoint from the small `972` primary — of 468 items with a `972` primary attribute only
17 also carry that attribute in the region, so the two are near-complementary sources, not duplicates.

## Status / next step

- **Decoded + validated:** `972`/`971` primary attribute (Str/Dex +1/+2/+3) AND the full `[2][9]` region
  (vitals + attributes + Magic Damage + Hit Rating, wire-scaled) — Royal-Signet-anchored.
- **IMPORTED (attributes, legacy):** the `972`/`971` primary sits on flattened `Item` columns -
  `StrengthBonus` / `ConstitutionBonus` / `IntelligenceBonus` / `WisdomBonus` / `DexterityBonus` (nullable
  `int`), populated by `RdbItemImporter` (only Str/Dex ever appear in `972`). Summed by
  `MaxStatAggregator.AggregateGearBonuses`.
- **NEXT (this decode):** import the `[2][9]` records into an `ItemStatModifier` child table
  (`ItemId, StatId, Amount`), whitelisted to the known stat ids, then (a) aggregate vital + attribute totals
  into the character's derived `MaxHealth`/`MaxStamina`/`MaxMana` (`EntityVitals`) so the regen bar fills past
  the class base — the #3 headline — and (b) patch attribute totals into the sent stat block. Spells almost
  certainly share this exact record encoding — see #367 (buff stat-effect engine), likely the same parser.
