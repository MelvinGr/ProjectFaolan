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

## Status / next step

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
  raises the ceiling and the regen bar starts full. ⚠️ **Needs in-client verification** of the client's
  max-computation model: this sends the *geared* max as the wire `HealthMax`. If the client instead adds gear on
  top of a base max (the #3 320→648 observation), that double-counts — flip to sending the base max on the wire
  and keeping only the regen ceiling geared.
- **NEXT:** patch attribute totals (`+Str`/`+Dex`) into the sent stat block so worn attributes show in-client.

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

So a live **apply/revert engine** needs a per-stat scale table **verified against in-client buff tooltips** before
the server can honor these (a blind apply would corrupt combat/vitals). The decoded `SpellStatEffect` table is kept
as the foundation; the apply engine is deferred to that verification (#367, `needs-testing`).
