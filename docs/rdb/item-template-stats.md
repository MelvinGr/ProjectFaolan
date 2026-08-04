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
(LE) with **strictly DECREASING fieldId**.

⚠️ **The field ids ARE wire stat ids** — this page said "NOT wire stat ids" until #626 showed the spell
record's are, and the two share several numbers (287, 294). Resolved against the client's own stat-name table
(LDB category 615, `--report ldb-stat-names`), 15 of the 18 ids listed here name something, and every name
matches the meaning the field was decoded to hold empirically:

| field | client's name | field | client's name |
|---|---|---|---|
| 973 | `Weapon Critical Damage` | 360 | `Scale` |
| 972 | `Weapon Attribute` | 316 | `Variance` |
| 971 | `Weapon Critical Chance` | 294 | `Casting Time (Seconds)` |
| 857 | `Secondary Attack Rating` | 287 | `Attack Range` |
| 499 | `Power Level` | 286 | `Min Damage` |
| 462 | `Enemy Damage` | 285 | `Max Damage` |
| 436 | `Damage Type` | 211 | `Equip Delay` |
| | | 148 | `Occupy Slots` |

(615, 496 and 460 have no entry in the table — an id the client never names, not a decode failure.)

⚠️ That makes the `971` reading below **doubtful**: the client calls it `Weapon Critical Chance`, not "the
amount for 972", and 971/972/973 read as a weapon triple in exactly the shape 347/348/349 do on a spell
record. Tracked in Gitea #631; the import already treats 971/972 as a last-resort fallback used only when the
`[2][9]` region is empty, so the blast radius is small.

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
**independently in-client-verified tooltip** (`docs/re/spawn-dissection.md` STEP (b), 2026-06-07): the
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

## Weapon min/max damage (#395) — property fields 285/286

The weapon's inherent damage is NOT a stat modifier (weapons carry no weapon-specific `[2][9]` record); it's a
pair of **property-table fields** (the strict-decreasing run, same space as 74/499/972):

| field | meaning | how it was pinned |
|-------|---------|-------------------|
| `285` (0x11D) | **MAX** damage | client `CoreCombatManager::GUIGetMaxDamage()` reads `getProp(0x11d, 2)` |
| `286` (0x11E) | **MIN** damage | client `CoreCombatManager::GUIGetMinDamage()` reads `getProp(0x11e, 2)` |

Pinned by Ghidra (tools/ghidra `StrXref.java` found the internal stat names *MeleeWeaponDamage*/*RangedWeaponDamage*;
the item property getters were reached via ConstScan on field 972 → the two named GUIGet\*Damage functions). The
GUI getters ADD character-stat modifiers on top (attribute/rating fields 0x348/0x3cc/0x36b), so 285/286 are the
weapon BASE; the client layers stats for display. Imported onto `Item.MinDamage`/`Item.MaxDamage` (460 weapons,
read via the strict property run; min ≤ max). Raw template values — **scale provisional** (`CombatResolver.WeaponDamageScale`
÷100, tune in-client). `CombatResolver.ResolveMeleeDamage` drives melee off the equipped weapon instead of the
flat level placeholder. `[V0: exact combat scale/formula — the real per-hit damage is a server-GDB formula, undecoded]`

## Implemented

- **Decoded + validated:** `972`/`971` primary attribute (Str/Dex +1/+2/+3) AND the full `[2][9]` region
  (vitals + attributes + Magic Damage + Hit Rating, wire-scaled) — Royal-Signet-anchored
  (`ItemTemplateStatModifierTests`).
- **IMPORTED:** both sources are unioned into the `ItemStatModifier` child table (natural key `(ItemId,
  StatId)`, `Amount` = raw wire value), populated by `RdbItemImporter` via `ItemTemplateStatModifiers.Parse`
  (the `[2][9]` region) plus the field-972 primary; the region wins on the rare overlap. Whitelisted to the
  stats a server consumer exists for today — the three vitals (HealthMax/StaminaMax/ManaMax), the five
  attributes, and the physical **Armor** rating `0x1c0` (#401) (`ApplicableStatIds`); the other decoded ratings
  (Magic Damage `0x35d`, Hit Rating `0x136`, the magical-protection `0x1c3`, …) are left unimported until a
  consumer lands. This retired the old flattened `Item.*Bonus` columns.
  `MaxStatAggregator.AggregateGearBonuses` sums the rows across equipped gear, dividing each by its
  `CharacterStatId.GetScale()` to a display total.
- **APPLIED (vitals):** at world entry `GetEquippedStatItems` -> `MaxStatAggregator` folds the gear vital totals
  onto the class/level base in `EntityVitals.FromCharacter`, seeding current == the geared max, so a `+HP` piece
  raises the ceiling and the regen bar starts full. This sends the *geared* max as the wire `HealthMax`.

## Spells share the region — but it is per-stat scaled (#367)

Buff/debuff spell templates (RDB `1040005`) carry the SAME `[2][9]` record region, decoded by the same
`ItemTemplateStatModifiers.Parse`. `RdbSpellImporter` imports it into the `SpellStatEffect` table (natural key
`(SpellId, StatId)`, `Amount` = raw wire value) — **863 spells / 1708 rows**. This region is the client's own
source for a buff's tooltip stat lines, so the stored `Amount` matches what the client renders.

The catch, found while scoping the apply engine: **the spell region is heterogeneous and per-stat scaled, and
its scale is NOT the item scale.**

- **Attributes store DISPLAY units** — `Dexterity Buff 1/2` = `0x32E` `+1/+2` (dumped: `--report spell-template
  --report-arg 3199,3200`). Items store the *same* attribute at `×10`, so `CharacterStatId.GetScale()` (built for
  the item convention) does **not** transfer to spells.
- **Movement / percentage / rank stats store large scaled magnitudes** — `0x1` = `190000` on every `Mounted`
  mount-speed buff; `Sustaining Wrath` ranks step `108000 → 180000`. A health entry (`0x1B`) even carries a
  `-100000000` drain/kill sentinel on non-buff spells.

A blind apply would corrupt combat/vitals, because the spell region's per-stat scale is not the item
`CharacterStatId.GetScale()`. The decoded `SpellStatEffect` table is the foundation. Open work is tracked in
Gitea #367.

## ~~Template-scaled items carry no region at all~~ — WRONG: the scan was word-aligned (#441, SOLVED)

**This section previously claimed high-level gear stored no stat records and derived its stats from level + quality +
slot. That was wrong, and the evidence for it was an artefact.** The records were in the templates the whole time;
`Parse` could not see them because it scanned only **4-byte-aligned** offsets, and most regions are not aligned to the
body — `Blackwolf Cap`'s first record starts at body offset **297**. An unaligned region yields *zero* hits from an
aligned scan, which reads exactly like "this item has no stats".

The doc even cited `Talons of Dark Terror` (3763061) as having "zero signature hits". Byte-granular, it has six
records — `312=30, 448=191, 507=13000, 806=380, 808=490, 861=1500` — and the client applies Con 380 / Int 490, exactly
those. Scanning byte by byte (`ItemTemplateStatModifiers.Parse`) took the corpus from **1398 to 4223** stat rows across
**1705 items**, and changed nothing for templates that were already aligned (`Royal Signet` stays 3 records, `Fine
Steelsilk Pants` stays 6).

Verified against the client's own applied stats for **18/18** items observed on the `ClientInterface` channel
(`docs/tooling/client-debug-logging.md`) across four characters. Regression gate:
`ItemTemplateStatModifierTests.FindsARecordAtEveryByteAlignment`, which builds the record at all four byte phases.

One consequence: the **field-972/971 primary attribute is now a last resort**, applied only when the region is
genuinely empty. It used to fill in whenever that one stat was missing, which — with the region parsing properly —
invents bonuses the client does not apply (`Obliteratus` records Con 360 / Int 800, and 972 added a phantom Str +1).

The property fields below are still real and still worth knowing, they are simply not a stat *source*:

| field | meaning |
|---|---|
| 54 | item level |
| 499 | quality tier (also the `ItemQuality` value) |
| 496 | per-item budget; tracks quality (1000 at tier 1, 2500/2000 at 2, 3250/3000/3500 at 3, 4000/5000 at 4, 6000 at 5) |
| 41 | slot mask - the same set differs only here: `Tanned Gauntlets` 1, `Tanned Boots` 2, `Tanned Tunic` 24 |
| category | armour class (30000 cloth, 30001 light, 30002/30003 heavier, 30020 neck, 30023 cloak) |

`--report item-stat-scaling` still writes them next to each item's records as CSV.

## An item identity can be INTERPOLATED or COMBINED (client-side, #441)

`FUN_005d52a0` — the describe/`ToString` for an item reference — shows the client's item identity is not always a
single template id. The struct is `[u32 templateA][u32 templateB][u32 templateC][i32 itemLevel]` and it renders as
one of two shapes:

```
itemLevel == -1  ->  "CombinedItem Templates: (A, B, C)"
otherwise        ->  "InterpolatedItem Level: <itemLevel> Templates: (A, B)"
```

So a procedurally-scaled item is expressed as **two bracketing reference templates plus an item level**, and the
client interpolates between them — which is the mechanism behind template-scaled gear carrying no stat records of
its own. `FUN_008cb620` (channel `GameCode/ItemData`) is the loader that reads and validates the item level
(property field `0x36` = 54), rejecting a template with level 0 (*"has item level 0! Reexport in the tool, need a
minimum of 1"*).

What this does NOT yet give us: which reference templates bracket a given item, and how the interpolation weights
the two. That is no longer needed for item STATS — those are read straight out of each template's own record region
(above) — but it is still the mechanism behind the class attribute BASE, which arrives as a per-class template the
client interpolates at the character's level. Open work: Gitea #444.
