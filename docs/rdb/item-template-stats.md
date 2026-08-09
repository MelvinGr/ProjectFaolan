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

### ⚠️ It is SEVERAL descending runs, and the first one holds field 0 (#756)

"A run" undersells it. A real template opens with three groups back to back, each descending on its own:

```
word 0    the SEARCH CATEGORY (30000 armor, 30023 cloak, ...) - not a field id
then      76, 30, 0 | 298, 88 | 645, 644, 499, 496, 360, 148, 74, 54, 41, 21
```

`RdbItemImporter.TryReadPropertyField` takes the **longest** run and rejects `fieldId == 0`, so it can only ever
see the last group — and field **0**, the client's `Flags` word, sits at the end of the *first* one, where
"strictly decreasing" holds and the `== 0` guard is what throws it away. `ItemTemplateProperties.Parse`
(`Faolan.Core.Rdb`) walks the whole table instead and returns every pair, strings included.

A **string** value is `[fieldId][0x21][len u16][flags u16][len bytes, padded to a word]` — field 21 is the display
name — and it **terminates** the table. What follows is the record's fixed regions (asset refs, the `[2][9]`
records), which are not pairs but are full of words that pass a "`<= 2000`, could be a field id" test. Walking past
the name does not stop, it *drifts*: it gave 200-odd templates a `Flags` of `0xE2000000` / `0x0007E200` /
`0x0BD30000` — the list-framing words `0x7E2` and `0xBD3` at three byte alignments, the trap CLAUDE.md warns about.
Stopping at the string removed every one of them (`--report item-binding`, 4688 of 4741 templates carry field 0).

### Field 0 `Flags` is the item's BIRTH stat-0 word

The client proves the link itself: `InventoryGame_c::AddTemplateItem` (`FUN_0076e9f0`) reads the loaded template's
flags dword and writes it straight into the new item's **stat 0**, masking out bit 11 and OR-ing in bit 4 when its
fourth argument is zero. Measured against the dumps, retail's server does the same — of **179** captured
`DispatchAddItem` arrivals, **174 carry exactly their template's `Flags`**, and four of the five differences are
*only* bit 4 (`0x10`), on mail/AH deliveries, which is the bit `AddTemplateItem` itself sets. The one genuine
exception is a claim redemption (`Exemplar's Cache` 5063647: wire `0x05000000`, template `0x00000000`).

| bit | templates carrying it | meaning |
|---|---|---|
| 0 | 4676 | set on virtually everything; unexplained |
| 24 | 197 | only ever seen alongside bit 26 |
| **26** | **1267** | **BOUND** — see [item binding](#item-binding-756) |
| 27 | 256 | co-occurs with 26; unexplained |
| 1 / 2 / 5 / 8 / 9 / 12 | 95 / 21 / 2 / 31 / 8 / 1 | unexplained |

## Item binding (#756)

The client names its own three tooltip lines. They are LDB **category 100** tokens whose token *names* sit
consecutively in the exe's `InventoryGUI\ItemInfoView.cpp` string block:

```
CurrentlyEquipped · DropsInPvP · BoundToCharacter · BindsOnPickup · BindsOnEquip · NotCancelable ·
CanNotBeUnbound · GemSlots

elf28("BoundToCharacter") = 202900962 -> LDB[100] "Character Bound"
elf28("BindsOnPickup")    = 174799584 -> LDB[100] "Binds when Picked Up"
elf28("BindsOnEquip")     =  45924736 -> LDB[100] "Binds when Equipped"
```

⚠️ None of those three STRINGS is in the exe's string table and none of the three token IDs appears as a literal
either — the client passes the token NAME and hashes it at runtime. Inverting `Elf28` over the exe's strings is
what recovers them; a `ConstScan` on the id finds nothing.

`ItemInfoView::UpdateFromItem` (`FUN_014d5980`) picks between them with two predicates:

| | how the client computes it |
|---|---|
| **binds on EQUIP** (`FUN_014d51a0`) | `getProp(177) & 0x20` **\|\|** (`getProp(499) > 1` **&&** `getProp(30) & 0x04`) |
| **binds on PICKUP** (`FUN_014d5210`) | `AcgItemData_t::GetInterpolatedItem(quad)[+0xC8] >> 26 & 1` — bit 26 of the `Flags` word |
| **is bound NOW** | the item's own flag test, `vt+0xA0` with `0x04000000` — i.e. **wire stat 0 bit 26** |

Field 177 is `More Flags`, 30 is `Can` and 499 is `Power Level` (the quality tier) in the client's own stat-name
table. In practice the `MoreFlags` clause fires on **0** templates and the quality clause on **2014** of 4741;
within it the `Can` bit is near-universal (3854 templates) and `PowerLevel > 1` is the discriminator (2374), so
the rule is really *"better than Common, and flagged equippable"*.

### The bound flag caught in the act

`[V2: demol80-demonologist_login_gameplay_annotated_3.tshark.json 09:46:44 and 09:47:00]` — an annotated capture
in which the player narrates both halves in `/say`:

```
09:46:44.757  DispatchSetItemStat  bag(104) slot 8      stat 0 = 0x00000001     "unbound: [Imbued Frayed Circlet]"
09:47:00.842  DispatchSetItemStat  equipped(101) slot 2 stat 0 = 0x04000001     "just eqpuied item, it is now bound"
```

Template 3721359's own `Flags` is `0x00000001` — so it arrived carrying exactly its template's word, and the
server re-sent stat 0 with **bit 26** added when it went on. That is the whole mechanism: `Flags` bit 26 is the
bound flag, an item whose TEMPLATE already carries it is born bound (= "binds when picked up"), and binding on
equip is the server setting that bit on the equipped cell.

⚠️ **One template contradicts the pickup half and is not resolved.** `Bindings of Dark Terror` (3762969) carries
bit 26 in its template, so it should read as bind-on-pickup, yet its in-client GM tooltip says *"Binds when
Equipped"* with no `Character Bound` prefix — which per the disassembled branch requires
`FUN_014d5210` to have returned **false**. That render was on OUR server, where every item ships `flags = 1` and
`template2` is flattened onto `template1` (#752), so the likely cause is `AcgItemData_t::GetInterpolatedItem`
failing to resolve the quad we send (its `iVar1 == 0` path returns false). Re-check in-client once the real `Flags`
word is on the wire. `[V0: whether an interpolated ACG item's flags can differ from template1's]`

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

### ⚠️ RETRACTED — 971 is not 972's amount (#631)

`971`/`972`/`973` are a coherent **weapon triple**, in exactly the shape `347`/`348`/`349` are on a spell record,
and we had press-ganged 971 into being its neighbour's amount:

| field | client's name | what we read it as until #631 |
|---|---|---|
| `973` | `Weapon Critical Damage` | unread |
| `972` | `Weapon Attribute` (a stat id as its VALUE) | the stat id of an attribute bonus |
| `971` | `Weapon Critical Chance` | the AMOUNT of that bonus (`+1`/`+2`/`+3`) |

**The client settles it.** Its unit-suffix helper `FUN_01119f20` takes a stat id and appends `"%"` for exactly the
ids listed in it and nothing otherwise (the suffix string at `01fbcf18` is literally `"%"`). **971 and 973 are both
in that list; 972 is not.** A crit chance is a percentage; a `+1 Strength` is not, so 971 was never the amount of
an attribute bonus.

That reading is gone from the import. **Nothing replaces it:** 972 names an attribute but states no amount — it
reads like the spell record's `ScalingAttribute`, i.e. what the weapon scales OFF rather than a bonus it grants —
and 971/973 are crit stats whose wire scale is unmeasured, so emitting them would be inventing a number. Field 972
does self-validate as a stat id (across all 1964 decrypted templates it holds an attribute id in **253** items and
a non-attribute value in **0**, in practice only Str (178) and Dex (75)), which is why the wrong reading looked so
solid; a value that is definitely a stat id is not thereby a bonus.

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

- **Decoded + validated:** the full `[2][9]` region (vitals + attributes + Magic Damage + Hit Rating,
  wire-scaled) — Royal-Signet-anchored (`ItemTemplateStatModifierTests`).
- **IMPORTED:** that one source fills the `ItemStatModifier` child table (natural key `(ItemId,
  StatId)`, `Amount` = raw wire value), populated by `RdbItemImporter` via `ItemTemplateStatModifiers.Parse`.
  The field-972/971 fallback was removed in #631 (403 rows), leaving the region as the only source. Whitelisted to the
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

One consequence: the **field-972/971 primary attribute became a last resort**, applied only when the region was
genuinely empty. It used to fill in whenever that one stat was missing, which — with the region parsing properly —
invents bonuses the client does not apply (`Obliteratus` records Con 360 / Int 800, and 972 added a phantom Str +1).
#631 then removed it outright: it was not a weakly-sourced bonus, it was not a bonus at all.

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

### What we ship (2026-08-09)

`Item.Flags` carries the harvested word — **4,688 of 4,749** items, **1,267** of them minting bound; it is
**nullable**, because 65 templates genuinely carry `0` and 61 items have no readable RDB entry, so a `0` cannot
also mean "unread". `SnapshotItems` sends it as stat 0 on both the snapshot entry and the live add, falling back
to `1` only for the null case. The `flags = 1` default parameter that no caller ever passed is gone — the third
time that exact shape shipped a wrong constant on this message.

Not shipped yet: the per-INSTANCE half. An item that binds when EQUIPPED needs the server to set bit 26 on the
equipped cell and re-send stat 0 (the message the capture above shows), and somewhere to persist that it has
bound. `ItemInstance.Binding` was **deleted** rather than repurposed — the decode says the per-instance state is
one bit, not the three-valued enum that column modelled — so the storage lands with the equip behaviour that
needs it. Gated by `ItemFlagsTests`; measured by `--report item-binding`.

