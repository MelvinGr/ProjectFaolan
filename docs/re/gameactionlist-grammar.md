# GameActionListIIR_t (0xF508F4C1) wire grammar — a property-map action list

Recovered from `AgeOfConan.exe` (Ghidra). The "GameActionList" is the per-dynel **spell/action list** — a
`0x3F1`-framed list of action elements, each a **keyed stat/property map**, not a flat header + tail.

## Reader chain

- `GameActionListIIR_t::ReadSubClass` (`FUN_008679f0`): calls the list reader `FUN_00870500`, then reads
  `[u8 flag]` (+0x3c) + `[u32]` (+0x40).
- **List** `FUN_00870500`: `[u32 c]` with the **`0x3F1` invariant** (`c % 0x3F1 == 0 && c > 0`, element count
  `= c/0x3F1 - 1`; asserts "Streaming in spell list with N elements"). So the `0x000007E2` seen in captures is
  `2 × 0x3F1` ⇒ **1 element**. Then `count` elements, each a 0x68-byte `GameActionData` object constructed by
  `FUN_008985c0` (the ctor — sets up the three typed property maps + defaults) and deserialized by `FUN_00899a60`.
- **Element deserialize** `FUN_00899a60`: the exact wire layout is in the section below (positional, driven by a
  static per-type descriptor; string values for keys `0x4d9`/`0x4da` — key indices 209/210 — u32 otherwise).

## Why the bodies looked multi-form

Each action carries a different set of properties (an animation action has a string name property; a
positional action has numeric properties), so the element length varies (4–91 B) — it's a property bag, not a
fixed record. The capture taxonomy (`GameActionListCodecTests`) reflects this: the clean
`[u16 len][anim name]` form is one string-valued property; the rest are numeric property sets.

## Element deserialize (`FUN_00899a60`)

The per-element reader is `FUN_00899a60` (called by the list loop in `FUN_00870500` right before the
`"Something went wrong while reading a GameActionData blob"` guard). `GameActionData` is a 0x68-byte object
whose ctor (`FUN_008985c0`) sets up **three typed property maps** — so a property's value type is which map its
key lands in:

- `fcVector_x<BinSearchMapKeyValue<uint,int>>`   (+0x1c) — int props
- `fcVector_x<BinSearchMapKeyValue<uint,float>>` (+0x2c) — float props
- `fcVector_x<BinSearchMapKeyValue<uint,String>>` (+0x3c) — string props (keys `0x4d9`/`0x4da`, e.g. `idle2_emote`)

`FUN_00899a60` reads (`FUN_0048f900` = read u32 from the stream). The layout is **positional, not key-tagged** —
the keys live in a static per-type descriptor, the wire carries only values in that type's order:

```
[u32 typeId]    -> obj+0x10   (the GameActionData type; must be 0xcf09..0xd026; looked up via FUN_008c3910)
[u32 field]     -> obj+0x14
[u32 == 4]      (validated, else "Invalid GameAction data version")
[u32 subCount]  (< 1001), then subCount × { criterion: FUN_006e9f20 + FUN_006e70e0 }
[u32 for 0x2d5][u32 for 0x2d6][u32 for 0x2f2][u32 for 0x2f5]   (four always-present int values, positional)
--- then the type-specific values ---
descriptor = FUN_008c3910(typeId)         # unknown type -> "Tried to stream unknown GameActionData with type N"
for each entry in descriptor.keyList (at descriptor+0xc, u32 entries, until an entry == 0xFF):
    key = keyTable[entry]                 # DAT_025073c0[] maps the key index -> the property key (see below)
    if key in {0x4d9, 0x4da}:  value = read std::string   (FUN_0048f840)   # the string props (anim names)
    else:                      value = read u32           (FUN_0048f900)
    map[key] = value
```

So the wire has **no keys and no terminator** — an element is `[typeId][field][4][subCount][criteria][4 fixed
u32][type-specific values]`, and the whole per-type layout is fixed once you know the type id + its descriptor
key list (the keys and the `0xFF` live in the static descriptor, not on the wire). Read at runtime by the handler via
`FUN_0089bd80(action, key)` — e.g. SetSpellRecastTime (`FUN_008b0f50`, string `GameCode/GameAction/SetSpellRecastTime`)
pulls keys `0x463` (caster/line) and `0x2f9` (recast centiseconds).

## The key index table — what a key list entry names

A descriptor's key list does not hold property keys; it holds an **index** into a 255-slot table the client
builds at runtime, `DAT_025073c0`, written by `FUN_00897bd0` as a flat run of
`MOV dword ptr [0x025073c0 + 4i], <key>` (244 of the 255 slots are filled). `DumpGameActionKeyLists.java`
reads that initialiser and emits the resolution as the tsv's **`statKeys`** column, so a key list can be read
without the indirection.

The resolved keys are **ordinary stat ids** — the same id space as `CharacterStatId` — which is what names them:
148 distinct keys are used across the 260 types and 120 of them already have a client name, most from the
`GameAction_*` run at `0x300`–`0x50B`. Confirmed against the one pair published independently: index 88 → `0x463`
and index 39 → `0x2f9` are exactly the two keys `SetSpellRecastTime` is decompiled reading.

**An entry of 1305 / 1307 is the key itself, not an index** (both are past the table's 255 slots). They resolve
to `0x519 GameAction_FloatValue1` and `0x51B GameAction_FloatMultiplier`, which is precisely what the client's own
field-name array calls those two slots — so the pass-through is the reading, not a hole in the table. (Masking the
entry with `& 0xFF` would fold 1305 onto index 25, `0x2eb`.)

The 28 keys with no name all sit in `0x2D2`–`0x2FE`, in the stat-name table's sub-768 range.

**What it settles for mounted combat:** exactly two of the 260 types ask whether they may be used from the
saddle — `Charge` and `ChargeSpecialAbility`, both carrying key index 245 → `0x4FD GameAction_CanUseIfMounted`.
There is no general "usable while mounted" property on any other action, so the client does not gate ordinary
casts on one.

## Decode coverage

- Header/list framing fully decoded + validated 925/925; `GameActionListCodec` round-trips element bytes verbatim.
- The full element grammar is recovered (positional, type-descriptor-driven; string vs u32 by key).
- **Type id + key list: recovered for all 260 types** — statically, from the initialiser `FUN_008bdf40`
  ([`gameaction-keylists.tsv`](gameaction-keylists.tsv)). Any element is byte-constructible.
- The spell base cooldown is sourced from here: it is the spell record's own `SetSpellRecastTime` /
  `SetSpellLineRecastTime` element ([spell-recast-source.md](spell-recast-source.md)).

## The descriptor table is statically recoverable, and it names the RDB modifier record

1. **Read the initialiser, not the table.** The descriptor table (`DAT_025077c8`) and the key-index table
   (`DAT_025073c0`) are BSS — zero in the static image — so reading `descriptor+0xc` out of the image returns
   zeros. The populator is `FUN_008bdf40`, one flat initialiser that writes all 260 descriptors — name, label, key list and (on ~20 types) the key list's own **field names**. It writes
   them two ways, which is why a single-form search missed it: the address is constant-folded for most types
   (`MOV [base+8], <enumId>`), and written through a running cursor for the rest
   (`PUSH <name>; MOV ECX,<enumId>; CALL FUN_008bde20` then `MOV [ECX + EAX*4 + 0xc + 4j], <key>`).
   `tools/ghidra/DumpGameActionKeyLists.java` walks it with constant-register tracking and emits
   [`gameaction-keylists.tsv`](gameaction-keylists.tsv) — **260 types, all with key lists**. Where it disagrees
   with [`gameaction_types.json`](gameaction_types.json) (a 68-type live Frida dump), **the static reading is the
   right one**: `AddProc` and `AddSpellProc` have six keys, not four, and the RDB record widths (below) confirm
   six. The descriptor's used part is `0xF4` bytes: name pointer at `+0x00`, id at `+0x08`, key list at `+0x0c`, a
   parallel field-name array at `+0x5c`, a third (always zero) at `+0xac`; the descriptor stride is `0x104`.
2. **The RDB feat/spell/item "stat-modifier record" IS a GameAction element.** The `[kind][0][4][0][1][0][2][9][…]`
   record that `ItemTemplateStatModifiers` parses is this exact element: `kind` is the GameAction type id, the `4`
   is the version word the deserializer validates, the `0` after it is `subCount`, and **`1,0,2,9` are the four
   always-present positional values** `v(0x2d5) v(0x2d6) v(0x2f2) v(0x2f5)`. What follows is one u32 per key-list
   entry — so the "6-word signature" is not a signature at all, it is a constant prefix, and the record's width is
   **`8 + len(keyList)` words**, not a fixed 10.

**Cross-validated on two independent sources.** Predicted width (`8 + len(keyList)`, from the client's
initialiser) against measured width (`--report feat-effect-widths`, from single-element RDB records) agrees on
**17 of the 19** kinds that occur:

| kind | name | keys | width |
|---|---|---|---|
| 53019 | `Spell` | `39` | 9 |
| 53045 / 53067 / 53110 / 53139 | `Modify` / `ModifyAsRating` / `TempChangeSkill` / `SetFlag` | `0`(Stat)`,39` | 10 |
| 53031 / 53069 | `ChangeRecastTime` / `ChangeLineRecastTime` | `187,39` / `1,39` | 10 |
| 53030 / 53075 | `ChangeHitPercent` / `ChangeLineHitPercent` | `187,39,55` / `1,39,55` | 11 |
| 53209 | `UpgradeCombo` | `187,66,67` | 11 |
| 53054 / 53062 | `SpellStatModifier` / `SpellLineStatModifier` | `88,0`(Stat)`,39,175`(Amount) / `1,0,39,175` | 12 |
| 53182 | `SpecialAbility` | `39`(Value)`,77`(Hash)`,84`(Level)`,174`(Substate) | 12 |
| 53213 / 53211 | `AddProc` / `AddSpellProc` | six | 14 |

**That measurement is an upper bound, not the width** — `recordLength - elementStart` also swallows whatever
the record carries after the element list, which is why an exact match on 17 kinds is the strong result and a
surplus is not automatically a disagreement. `--report feat-effect-widths` prints the surplus words: `Modify`'s
wide variants are followed by a pair of list frames (`0x7E2`, `0x3F1`), and `AddSkinnedMesh` (measured 12 words,
predicted 9) by `2 9 207912`.

### Widths measured by tiling — exact, on 27 kinds

The bound above only exists because a single-element record has no end marker but its own. Elements sit **back to
back with nothing between them**, so a record holding several measures the non-final ones *exactly*: the distance
from an element's opening type word to the next element's IS its width. The same report now makes that second,
independent measurement over 15,987 element regions, tallying a width only where the region's own `0x3F1` frame
count agrees with the elements the signature walk found (15,880 of them do) — agreement being the record's own
cheap proof that two elements really are adjacent rather than separated by trailing content.

It reaches **27 kinds against the ceiling pass's 17**, all exact, and settles both of that pass's ambiguities:

- `AddSkinnedMesh` tiles at exactly **9 words**, so its 12-word ceiling was trailing record content — *not* the
  string-valued key it looked like.
- `Charge` never occurs as a non-final element, so its lone 8-word reading stays what it was: probably a
  coincidental signature hit rather than an element.

### The width model has one exception: a text slot

`8 + len(keyList)` describes an element whose every slot is a u32. **Keys `209` and `210` (`EventString`) are
text**, written `[u16 byteLength][bytes]`, which makes any element carrying one variable-width — and variable by
an odd number of bytes, so the payload has to be walked slot by slot rather than indexed. `SetMorphValue`'s
"ASCII mesh name after the element" was this slot *inside* it all along.

**The set is closed, and read out of the client** `[V1: tools/ghidra/DecompileFns.java]`. The deserializer
`FUN_00899a60` takes the string branch for exactly two `GameActionStat_e` ids, `0x4d9` and `0x4da`; the key-index
table it reads (`&DAT_025073c0`, built by `FUN_00897bd0`, 244 of its 255 slots written as constants) maps index
**209 → `0x4d9`** and **210 → `0x4da`**, and nothing else to either. So a type is variable-width iff its key list
contains 209 or 210 — `GenerateName` and `NewTextEffect` are the only two carrying both.

**The length word is a u16, not a u32, and there is no second form.** Same source, settled on both sides of
the stream `[V1: tools/ghidra/DecompileFns.java]`: the read is `FUN_0048f840` → `FUN_00cbf840` mode 0, which
reads a u16 and treats `>= 0x8000` as an escape into a long form no shipped template uses, and the **writer**
`FUN_00899440` emits that same u16 with the same escape (`0xFFFF`, then `0x01FF`, then the u32). `FUN_00cbf840`
*does* have a u32 mode — `param_2 == 2` reads the length through `FUN_0048f900` — but nothing on the GameAction
path passes it, and neither side branches on the type: the string branch is chosen by the stat key alone. So a
u32-length string cannot be produced or consumed by this client. The records agree — of the 880 text-carrying
elements in the feat and spell catalogs, **653 tile exactly** under the u16 (the distance to the next element's
type word equals `32 + 4×fixedSlots + 2 + len`), and the strings come out whole with the slots after them
aligned:

| element | payload, by key |
|---|---|
| `SetMorphValue` in spell 4609400 | `209="arm" 39=-10` — 41 B |
| `StartParticleEffect` in spell 3832933 | `209="spell_curseofflies" …` — the 0x12-byte slot is exactly the name |
| `StartServerSideScryScriptWithParams` in spell 325651 | `39=227065 209="spawnHash=NSAO;pointCost=1;petBuff=783738;spellID=325651"` |

None of them carries a NUL inside the counted length.

`GameActionTypes.TextKeys`, `GameActionType.IsFixedWidth` and `ItemTemplateStatModifiers.ParseElements` implement
this; `--report feat-effect-kinds` prints the audit, bucketing every text-carrying element by which length word
makes it tile to its measured width.

**Seven elements do not tile under it** — all of them `HeadText` (53057), reading `[u32 7]["Dimach\0"][u32 3]`
with the NUL counted, two bytes wider than the u16 form allows. They are **retired data, not a second form**.
Three things put them there rather than in the grammar:

- the client has one string branch and it is a u16, on the read side and the write side both (above), so it
  cannot have written these and cannot read them back;
- they cluster with the other two stale shapes. All seven sit in feat templates `3753` and `12362..12386`, and
  six of those same templates also carry the retired kind **53044** (a string under a type id the client
  registers nothing for — see [the id space](#the-full-type-id-catalog--260-types-and-the-id-space-the-client-asserts))
  and a `ChangeHitPercent` padded out with four terminator words. Feat 12362's element region runs
  `53030`(+4 pad) → `53057`(u32 string) → `53002` → `53044`(u32 string): the one element in it that the current
  catalog reads cleanly end to end is the one with no stale shape;
- they are separated from live data by two orders of magnitude of template id. Every u16 text element in either
  catalog is id **791547 or higher**; every u32 one is **12386 or lower**.

The walk stops at such a slot rather than guessing a second length width, so the element is still located and its
earlier slots still read. `--report feat-effect-kinds` prints every member of each bucket, which is what makes
"all seven are one kind, in one id band" checkable rather than asserted.

### Two shipped-data anomalies, both confined and both characterised

Tiling leaves exactly two kinds disagreeing, and neither is the model being wrong:

- **`SpellStatModifier` (53054) at 10 words, ×7** — feat `Swift Pathing` ranks 4–10 (templates 5031395..5031401).
  Each record holds two of them tiling it end to end at ten words, and their payload is a plain `(Stat, Value)`
  pair: **Ride Skill** `5,7,8,10,11,13,14` and **Player Flags** `5`. Those are byte-identical to what the
  `Basic Riding` family (115944..115953) carries at the same ranks under the *correct* type words — `Modify`
  (53045) and `SetFlag` (53139), both two-key/ten-word types. Ranks 1–3 of the same family (5031392..5031394,
  234 B) carry genuine twelve-word `SpellStatModifier` elements (casting time on spells 3663303 / 4054483). So
  **the type word is stale, not the width**: it survived from ranks 1–3 while the payload was replaced with
  `Basic Riding`'s. Read at twelve words, the first element's `Value` slot lands on the following element's
  `0xCF3E`, which is what `RdbFeatImporter.ParseSpellStatModifiers` rejects on.
- **`ChangeHitPercent` (53030) at 15 words, ×6** — the `Dimach Item k1/k2/k3` crafting entries (e.g. 12362). Its
  three values (43464, 100, 100) are followed by **four `0x000000FF` words** — the key list's own terminator value,
  as padding. Same six templates as the u32-string `HeadText` and the retired kind 53044 above, so it is the third
  face of one stale authoring vintage rather than a third puzzle.

Neither is reachable as a stat modifier: the importer rejects both on general rules (a magnitude that is a
registered GameAction type id was never ours; a stat outside the id range likewise), not by name.

**Key indices that carry a name** (recovered from the types that name their own slots; a name transfers by key,
so it resolves that slot on every type that uses it): `0` Stat, `11` Radius, `17..20` AudioEffect*, `24`
SkillType, `25` TimedLength, `28..30` Rel{X,Y,Z}Pos, `72` TimeExist, `73` Flags, `77` Hash, `84` Level, `103`
NPCMaster, `174` Substate, `175` Amount, `209` EventString, `1305` FloatValue. Key `39` is deliberately left
blank: it is `Value` on a modifier and `ScriptID` on a script action, so it carries no transferable name. Key `1`
is unnamed but is the **spell line** by position — it is the slot that distinguishes every `…Line…` type from its
per-spell twin.

### A key index is not the property id — it indexes into `GameActionStat_e`

A key-list entry is an index, not the property id the client stores under. `FUN_014935c0(byte keyIndex)` maps one
to the other through `DAT_0297cd48` (asserting `newStat != 0`, from
`CoreGameFramework/GameActions/GameActionStats.h`), and `FUN_0077a510` is `GetIntStat(GameActionStat_e)` — its
own log line reads *"Tried to GetIntStat for GameActionStat_e … but it did not exist, returning 0"*. The table is
runtime-built by `FUN_01492c20`, which writes 243 of its 255 slots as constants, so it reads statically:

| key index | `GameActionStat_e` | name |
|---:|---:|---|
| 0 | 722 (`0x2d2`) | Stat |
| 1 | 723 (`0x2d3`) | Line |
| 39 | 761 (`0x2f9`) | Value |
| 88 | 1123 (`0x463`) | Spell |
| 172 | 1204 (`0x4b4`) | ramp low level |
| 173 | 1205 (`0x4b5`) | ramp high level |
| 174 | 1206 (`0x4b6`) | ramp low value (the client calls it `Substate`) |
| 175 | 1207 (`0x4b7`) | Amount — and the ramp's high value |

Indices 172…174 are **derived**, not read out of the table: the interpolation code below reads exactly
`{0x4b4, 0x4b5, 0x4b6, 0x4b7}` for a type whose key list is exactly `{0, 174, 172, 175, 173}`, so the four map onto
each other; `175 → 0x4b7` is already known, `0x4b6` is the lerp's low endpoint and holds a magnitude (which of the
remaining three is the second magnitude is settled by the shipped values), and `0x4b4 < 0x4b5` matches
`172 < 173` in every record. Verify before leaning on it for a type outside this family.

**The `GameActionStat_e` id is not an LDB-615 stat id**, however much it looks like one — 722…1269 lands inside
that table's id range and one slot even collides with a real name (`84` → 1119 → "PvP Cold Invulnerability
Rating"), which makes "just look the id up in LDB 615" a tempting and wrong way to name the remaining unnamed
slots (`55`, `66`, `67`, `117`, `236`, `237`). What LDB 615 names is the value stored under key `0`, not the key.

### What `Value` (39) and `Amount` (175) mean — from the tooltip builder

`Misc\ItemInfoCreator.cpp` renders a `SpellStatModifier` / `SpellLineStatModifier` / `0xD023` element at
`014a25a6`, reading exactly three of its four slots — `GetIntStat` on `0x2d2`, `0x2f9`, `0x4b7`, i.e. Stat, Value,
Amount — and branching on `Value == 0`:

- **`Value` set** → the generic stat-delta formatter `FUN_0149a300`, which appends the stat's own unit suffix
  (`FUN_014a6240(stat)`) and divides by a per-stat display scale. So `Value` is an **absolute** delta in the
  stat's units.
- **`Value == 0`** → an inlined copy of the same routine applied to `Amount`, but with the suffix replaced by the
  **hardcoded `"%"`** at `0x01fbcf18` — on all three of that branch's print paths (`014a27e8`, `014a2888`, and the
  `CastingTimeSeconds` path at `014a267a`, which prints `abs(amount) * 0.01` under `"%.1f%%"`). So `Amount` is a
  **percentage**, in hundredths of a percent.

The stat id itself is resolved through `GetLocalisedString(615, stat)` — `PUSH 0x267` at `014a2714` — which is
what ties this word to the ordinary wire stat space (`CharacterStatId`) rather than a spell-local one.

Key `55` (`ChangeHitPercent` / `ChangeLineHitPercent`, values 1 or 2) is read only by the element comparator
`FUN_014a5670`, never by the tooltip: it participates in sorting/grouping and carries no rendered magnitude, so
kind 53075's shipped `(line, percent)` decode does not depend on it.

### `UpgradeCombo` (53209) — key `66` is damage, key `67` is cooldown

The client leaves both slots unnamed in its descriptor, and the pair view (payload words 0 and 1) reads
`(combo, 66)`, which makes the 3,235 records look like a pure reference list with the magnitude elsewhere; the
magnitude is in the slot the pair view cannot see.

The item-tooltip builder `FUN_0149e450` names both by rendering them. It reads `GameActionStat_e` `0x315` and
`0x316` — which the key-index table `&DAT_025073c0` maps to key indices **66** and **67** — and branches on each
value's sign to pick a localised string:

| slot | `GameActionStat_e` | zero | `> 0` | `< 0` |
| --- | --- | --- | --- | --- |
| key `66` | `0x315` | skipped (`TEST/JZ` at `014a184f`) | `UpgradeCombo_IncreaseDamage` | `UpgradeCombo_DecreaseDamage` |
| key `67` | `0x316` | skipped (`TEST/JZ` at `014a192f`) | `UpgradeCombo_IncreaseCooldown` | `UpgradeCombo_DecreaseCooldown` |

Both are rendered `CVTSI2SS` → `MULSS` by the `0.01` at `0x01f2800c` with the hardcoded `%` at `0x01fbcf18`, so
both are **signed hundredths of a percent** — the same scale kind 53075 uses. `[V1: tools/ghidra/FindSym.java]`
`[V1: tools/ghidra/DisasmWin.java]` `[V1: tools/ghidra/DisasmFn.java]`

The shipped catalog agrees: `Dread Lord` ("Reduces the reuse time of all combos") carries key 67 = `-100` on the
whole `Leech Life` ladder and key 66 = 0, while `Hexer` ("improves the damage inflicted by the Blood for Aid
combos") carries key 66 = `+100` on the `Blood for Aid` ladder and key 67 = 0. Only 5 of the 3,235 records set
both. Imported as `FeatComboModifier`.

**These names are `UpgradeCombo`-scoped and do not transfer.** A key the client names (0 `Stat`, 39 `Value`,
88 `Spell`, 175 `Amount`) means the same thing wherever it appears; an unnamed one carries no such promise, and
the catalog shows it does not hold. Key `187` is a per-spell target on `ChangeRecastTime` (53031) and
`ChangeHitPercent` (53030) but a per-combo one here; key `66` is a follower script group on
`SetFollowerScriptGroup` (53024) and the proc direction on the `AddProc` family; and on
`ChangeInheritedWeaponAttack` (53250) key `67` is the combo attack's critical chance, not a cooldown. Name an
unnamed key `<Type>_<slot>` and keep it inside that type's parser.

**And the scale transfers even less than the name does.** 53250 reads keys 66 and 67 from the same two
`GameActionStat_e` slots this type does, and renders them through different arithmetic — see below. Same slot,
one type apart, three different units.

### `ChangeInheritedWeaponAttack` (53250) — one attack of a combo, on four axes

The sibling of `UpgradeCombo`, and not a variant of it: 53209 moves a combo as a whole, this moves one of its two
weapon attacks, along axes 53209 has no slot for. 315 records over 12 feats. Its branch of `FUN_0149e450` is at
`014a1dc5`, which reads all six slots up front and then renders one localised string per non-zero magnitude under
an attack heading:

| slot | `GameActionStat_e` | what it is | rendered |
| --- | --- | --- | --- |
| key `187` | `0x4C3` | the combo — 110 of the 112 distinct references resolve against `Combos.Id` | a `comboref://` link |
| key `84` | `0x45F` | **which attack**: `0` → `FinishingComboAttack`, `1` → `OpeningComboAttack` | the heading |
| key `66` | `0x315` | the attack's **DPS** delta, signed | raw int + `%` — a whole percent |
| key `67` | `0x316` | the attack's **critical chance**, signed | `× 0.025` (the float at `0x020b5944`) + `%` |
| key `68` | `0x317` | the attack's **hate adjustment**, signed | raw int + `%` — a whole percent |
| key `69` | `0x318` | the attack's **damage type** | `GetLocalisedString(600, value)` — a name, not a number |

Each magnitude has an `Increase…` / `Decrease…` pair picked on the value's sign (`IncreaseComboDPS`,
`IncreaseComboCriticalChance`, `IncreaseComboHateAdjustment`), and a zero is skipped by the same `TEST/JZ` idiom
53209 uses. `[V1: tools/ghidra/DumpStringsInRange.java]` `[V1: tools/ghidra/XrefsTo.java]`
`[V1: tools/ghidra/DisasmWin.java]`

**Key 84's descriptor label is `Level`, and it does not mean a level here.** The branch does
`TEST EDI,EDI` → `FinishingComboAttack`, `CMP EDI,1` → `OpeningComboAttack`, and **returns** on anything else
(`JNZ` at `014a1e46`), so it is a two-valued selector: a third value renders nothing at all. The shipped catalog
corroborates the polarity by name — `Fierce Opening` and `Dual Master`, whose own text is *"increases the
**opening** damage done by all combos"*, are the ones carrying `1`.

Exactly one magnitude is non-zero on all 315 records: key 66 on 212 (`Fierce Opening` 30, `Dual Master` 45/75,
`Smash` 10/20/30, `Crushed Bones` 5…25), key 67 on 101 (`Brutal Blows` 400 = +10%, `Brutal Maiming` 1100,
`Rupture Armor` 4000), and key 68 on the two rows of the Guardian's `Improved Shield Mastery` — the only feat in
the catalog that touches threat, which is exactly the feat that should. Key 69 is zero throughout. Imported as
`FeatComboAttackModifier`.

### The proc family — key `88` is the carrier's spell, key `236` the target's

`AddComboProc` (53212) and `AddSpellLineProc` (53070) carry two spell slots, and which is which comes from the
same builder. It reads `GameActionStat_e` `0x463` and `0x4f4` — key **88** and key **236** — tests each for a
valid spell **independently**, and picks one of three localised strings from the pair of results:

| key 88 set | key 236 set | string |
| --- | --- | --- |
| yes | yes | `AddComboProcBoth` |
| yes | no | `AddComboProcSelf` |
| no | yes | `AddComboProcOther` |

So key 88 is the spell the proc casts on the **carrier** and key 236 the one it casts on the **target**. Both are
real in the shipped data — 171 of the 876 records are target-only — which is why a parser may not require key 88.
`[V1: tools/ghidra/FindSym.java]` `[V1: tools/ghidra/DisasmWin.java]`

Key `117` is the trigger: the neighbouring `AddProc` branch (`FUN_0149e450` at 0x0149ef57, `[V1: tools/ghidra/DisasmWin.java]`) is the
mapping: it reads key `117` into a three-way switch — **0 → `AddProcOnHit`, 1 → `AddProcDamage`, 2 →
`AddProcSpellDamage`** — and key `66` (`== 1`) into the flag that swaps each for its `AddProcGetting…` twin (the
defensive side, which neither 53212 nor 53070 carries). Value **3** is the switch's default and has no sentence in
this family; on 53070 it is named by the feats that carry it, all on healing lines ("after casting the Wave of
Life…"), so it is **Cast**. The two forms use disjoint halves — 0/1 on combos, 2/3 on lines — and `FeatProcTrigger`
is the enum. Key `237` is **Procs per Minute**, the client's own label in the composed 53070 sentence.

### The interpolated family is a level ramp — keys `174`/`172`/`175`/`173`

`InterpolatedModify` (53220), `InterpolatedTempChangeSkill` (53221) and `InterpolatedModifyAsRating` (53068) all
carry `[0 Stat, 174 Substate, 172, 175 Amount, 173]`, and the client's own names for two of those slots
(`Substate`, `Amount`) describe neither. Decompiled from the apply function `FUN_008b2320` — located by its
`"InterpolatedModify target not a character.."` log line — and its two helpers `FUN_008c3970` / `FUN_008c39f0`
(`[V1: tools/ghidra/StrXrefs.java]`, `[V1: tools/ghidra/DisasmFn.java]`):

```
FUN_008c39f0(x):                       # the interpolation FRACTION
    lo = GetIntStat(0x4b4)             # key index 172
    hi = GetIntStat(0x4b5)             # key index 173
    x  = clamp(x, lo, hi)              # CMOVL / CMOVG at 008c3a20 / 008c3a25
    return hi == lo ? 1.0 : (float)(x - lo) / (float)(hi - lo)

FUN_008c3970():                        # what is interpolated OVER
    x = element[0x4c]
    if x == 0: x = caster.GetStat(element[0x40] == 1 ? 150 : 965, 0)
    return FUN_008c39f0(x)

FUN_008b2320:                          # the apply
    t    = FUN_008c3970()
    low  = GetIntStat(0x4b6)           # key index 174
    high = GetIntStat(0x4b7)           # key index 175 `Amount`
    stat = GetIntStat(0x2d2)           # key index 0   `Stat`
    delta = (int)((float)(high - low) * t + (float)low)
```

Stat **150** is `Casting Level` and **965** is `Apprentice Level` (LDB 615, `--report ldb-stat-names`) — both
effective-level stats — so the element reads:

> modify `Stat` by a value ramped linearly from `174` at level `172` to `175` at level `173`, clamped at both ends.

The shipped data agrees independently and could not by chance: across all 341 feat and 376 spell elements, keys
`172`/`173` only ever hold `10`/`20`/`60` and `80`/`100`, and consecutive ranks of one spell tile the level line
without gaps or overlaps while their values stay continuous across the seam — `Battle Cry: Marshal the Ranks`
runs `26@10 → 42@24`, `44@25 → 60@39`, `62@40 → 78@54`, `80@55 → 96@69`
(`--report feat-effect-kinds --report-arg 1040005/53220`).

This is why an element's magnitude cannot be taken positionally: word 1 is the ramp's low endpoint here and the
whole magnitude on `Modify`. `ItemTemplateStatModifiers.ParseStatModifiers` reads by key and returns the whole
ramp beside that endpoint (`Modifier.Ramp`); `LevelRamp.ValueAt` is the lerp above, and `FeatEffect` /
`SpellStatEffect` carry the other three slots as nullable columns.

### The mode word `element[0x40]` is not in the template

`FUN_008c3970` picks between stat `150` and stat `965` on `element[0x40]`, and that word is **not a slot of the
record** — it is word 0 of a **9-word invocation context** the GameAction is constructed with. `GameActionBase`'s
constructor `FUN_0089b390` block-copies those nine words into the live object at `+0x40`…`+0x60`
(`for (iVar6 = 9; ...) *puVar8 = *puVar7;` with `puVar8 = param_1 + 0x10`, i.e. byte offset `0x40`), and the RDB
deserializer `FUN_00899a60` never writes them: everything it reads goes into the key-addressed maps at `+0x1c` /
`+0x2c` / `+0x3c`, plus the four always-present ids `0x2d5`/`0x2d6`/`0x2f2`/`0x2f5`.

The word is generic, not interpolation-specific — the plain `ModifySkill` apply `FUN_0089dfd0` reads the same
`[+0x40]` through the **same** `1→3, 3→2, 4→1, 5→4` switch before calling the stat sink `FUN_007186a0`, and it is
mode `1` alone that takes the `GetOnCasterModifyPercent` / `GetOnTargetModifyPercent` branch. So mode 1 is the
cast path, which is why it is the mode that ramps over `Casting Level` while every other mode uses `Apprentice
Level`. Both resolve to the character's level on a server that does not model mentoring, so there is nothing to
import and nothing to store.

## Descriptor lookup and registration

`FUN_008c3910` is the descriptor lookup: `descriptor = 0x025077C8 + (typeId - 0xCF09) * 0x104`; `descriptor+8 == 0`
means "unknown type". `FUN_00899440` is the serializer and `FUN_00898890` a by-name accessor; both consume the
table. `FUN_008bde20` is the out-of-line registrar the initialiser calls for 89 of the 260 types: it writes
`descriptor+8 = typeId` and stashes the descriptor in `DAT_0297d418` (a "currently-registering" global), after which
the key list is appended at `descriptor+0xc`. A GameAction class's own constructor can have no references at all
(e.g. `FUN_008b0e10`, the ctor behind the `SetSpellRecastTime` handler `FUN_008b0f50`'s vtable `0x01fd7a88`) —
objects are created by a data-driven factory — so the initialiser, not the classes, is the route to the table.

Because the tables are BSS, a live read (Frida under Wine, no ASLR, so Ghidra VAs == runtime:
`tools/frida/dump_gameaction_types.py` + `dump_gameaction_names.py`) also works, but it reaches only the types
registered at dump time, and it must use the `0x104` stride (see below).

## The full type-id catalog — 260 types, and the id space the client asserts

[`gameaction-type-ids.tsv`](gameaction-type-ids.tsv) lists every GameAction type the client registers. It extends
the 68-entry `gameaction_types.json` in coverage only — the ids are that file's `enumId`, which it agrees with on
all 68 shared names. (That file's **`typeId` column is not a wire id** — see the stride trap below.)

**The client bounds the id space and asserts it.** Every reader checks
`i_nGameAction >= e_Type_GC_GameAction && i_nGameAction < e_Type_GC_GameAction_End` (`SpellData.cpp:1577`),
compiled as `(id - 0xCF09) <= 0x11D`:

```
LEA EAX,[ESI + 0xffff30f7]   ; index = id - 0xCF09
CMP EAX,0x11d                ; valid iff index <= 0x11D
...
IMUL EAX,EAX,0x104           ; descriptor = 0x025077C8 + index * 0x104
ADD EAX,0x25077c8
```

So the valid ids are **`0xCF09..0xD026`** — 286 slots, of which 260 are registered. Failing the assert is not a
dropped packet: it pops an "Assertion Failed" dialog and takes the client down. `GameActionTypeId.Validate`
enforces the range before anything is built.

### The other 26 slots are retired ids — the catalog is complete

The table is a flat array indexed by the id, so an in-range id is either registered or a **hole**; there is no
third state. 26 ids are holes: `53001, 53017, 53039, 53044, 53126, 53127, 53131, 53133, 53142, 53143, 53145,
53146, 53147, 53149, 53151, 53156, 53157, 53158, 53161, 53181, 53186, 53216, 53248, 53258, 53279, 53280`.

That they are the client's holes rather than the recovery's blind spots is the load-bearing claim, so it is
checked three ways `[V1: tools/ghidra/DumpGameActionKeyLists.java]`:

1. `FUN_008bdf40` writes 260 of the 286 slots and never mentions these ids;
2. a whole-image scan for the id constant finds **zero** hits (`ConstScan CONST=cf34`) — not in the initialiser,
   not anywhere;
3. the image holds exactly **260** `e_Type_GC_GameAction_*` name strings, matching the 260 descriptors, so every
   type the client can name was reached.

A retired id therefore has no descriptor, no key list and no name in the client at all. Both the element reader
`FUN_00899a60` and the writer `FUN_00899440` test the slot's id word for zero and log *"Tried to stream unknown
GameActionData with type N"* instead of reading a payload — so shipped data carrying one (kind **53044**, in seven
low-id feat templates) is content this client cannot parse. `GameActionTypes.UnregisteredIds` mirrors the list and
`GameActionKeyListTests.TheRegisteredTypesAndTheRetiredIdsPartitionTheIdSpace` keeps the two sets a partition of
the range, so a later re-dump that recovers one of them fails the build rather than silently re-opening the question.

`FUN_008bdf40` initialises the table, writing the name pointer at `descriptor+0` and **the id at `descriptor+8`**.
Reading the id from `descriptor+8` and deriving it from the address both give the same answer for all 171 inline
registrations; the other 89 register via `CALL FUN_008bde20`, which passes the id in `ECX`.

### The stride trap — `0x104`, not `0x41`

The descriptor stride is `0x104` (`FUN_008c3910`: `IMUL EAX,EAX,0x104`). Because **`0x104 == 4 * 0x41`**, a wrong
`0x41` stride still divides evenly — so "the address lands exactly on a descriptor" is worth nothing as a check, and
every id derived that way comes out **four times too far up the table**. That is exactly what
`gameaction_types.json`'s `typeId` column is: `53005 + 4*(enumId - 53002)`. An id derived that way (e.g.
`StartFlymode` as `0xD12D` instead of `0xCF92`) can pass a cross-check against another derivation sharing the same
error, and crashes the client when sent; only the client's range assert catches it. For a table index, validate
against the client's own bounds, never against another derivation of the same quantity.

## The four fixed ints: `0x2d5` is duration and `0x2d6` is interval

The element's four always-present values are read positionally (`[0x2d5][0x2d6][0x2f2][0x2f5]`), and the first
two are named by the client itself. `FUN_0089b390` reads `0x2d6`, finds `0x2d5` zero, and logs:

```
"Fixing broken gameaction data, Duration was 0 while Interval was " <0x2d6> ". Setting Duration to 1"
```

So `0x2d5` = **Duration**, `0x2d6` = **Interval** — and they are read as a pair. The executor `FUN_00869f90`
confirms the pairing: it runs an element inline only when neither is set, and schedules it otherwise —

```
0086a286  MOV ECX,0x2d6 ; CALL 0x0077a510 ; TEST EAX,EAX ; JG  -> schedule
0086a297  MOV ECX,0x2d5 ; CALL 0x0077a510 ; CMP EAX,1    ; JG  -> schedule
```

i.e. `interval > 0 || duration > 1`.

**Duration is a repeat count, not a time.** Over the whole catalog it takes `1` (run once, 56,769 elements),
`-1` (unbounded, 4,389 of the 5,039 elements that carry an interval) and a thin tail of small integers — 2, 3, 4,
5 … 40. Nothing takes a centisecond-shaped value.

### What that settles about a chain edge

`CastNano` links that carry an interval split cleanly into a cadence and a delay, by row:

| | rows | meaning |
|---|---:|---|
| `Repeats == -1` | 263 | a **cadence** — re-run every N while the carrier is up |
| `Repeats == 1` | 220 | runs **once**, so the interval is a **delay** |
| 2 / 4 / 9 | 3 | that many runs |

The interval alone cannot answer it; the duration word is the discriminator. `SpellAreaEffect.Repeats` carries
it.

## An RDB spell record holds several of these lists, and its elements carry criteria

The reader chain above is the wire one, but the RDB spell template (`1040005`) stores the same shape — and it
stores **more than one list per record**, which is invisible to anything that streams the element region.

### The list header

Each group of elements is introduced by a 12-byte header immediately before its first element:

```
[u32 2][u32 listId][u32 (count + 1) * 0x3F1]
```

That third word is `FUN_00870500`'s own count word, the one whose assert reads *"Streaming in spell list with N
elements"* — so this is not a new framing, it is the list reader's framing appearing once per group.
`ItemTemplateStatModifiers.ParseElementLists` walks it; `--report spell-element-lists` scores it:

```
records whose lists TILE exactly  : 19,076 / 19,413 = 98.26%
elements inside a framed list     : 60,895 / 61,840 = 98.47%
lists per record                  : 1 x 17,746   2 x 1,309   3 x 19   4 x 2
list ids                          : 0 x 18,738   20 x 1,142   33 x 502   31 x 33   29/14 x 6   30/5 x 1
```

The list ID is carried, **not** interpreted. It skews hard by kind — `0` is the bulk (57,580 elements),
`20` is `StopParticleEffect` / `Fatality` / `CastNano`, `33` is `SetSpellRecastTime` and the area/cone formulas —
which reads like a trigger (on-expiry, on-hit), but nothing has pinned that, so no consumer branches on it.

The remaining 1.74% break on a second structure, not on the header: a recurring 32-byte block
`[22][36][0x7E2][3][0x7E2][0][id][93]` that sits between lists (twice in `Spirit of the Bear (Rank 1)` 146262)
and once at the end of `Hellfire Stream` 8600. Undecoded.

### The sub-records are criteria — and they are what an "exact duplicate" really is

`FUN_00899a60`'s per-element sub-record reader is `FUN_006e9f20`. It reads **three u32s**, bounds the third to
`0..0x401`, and on failure logs

```
"invalid criterion id/value/operator ("
```

so the triple is `[id][value][operator]`, in that order — a condition on the element. `Element.Criteria` is the
typed view.

1,117 elements have decoded slots that repeat another element of the same record. Reading their criteria:

```
byte-identical incl. their criteria :   130
the same effect under different conditions : 987
across two different LISTS          :    42
```

So the overwhelming majority are not duplicated data — they are the same effect under different criteria, and a
reader that stops at the decoded slots cannot tell them apart. `AddProc` at `1006601@480` is the clean case: its
twin's criteria are identical except that the operators `93` and `103` are swapped between two ids.

Across the catalog: **74,593 criteria over 42 ids and 50 operators**. Half of them (37,540) carry **no operand** —
`[0][0][op]`.

**The evaluator is recovered and both vocabularies with it** — `FUN_006dd970` is `CriteriaItem_t::CheckCriteria`,
it walks the run as a **postfix expression over a boolean stack**, and the no-operand half is exactly its logic
operators plus a family of subject selectors that push nothing. A criterion's `id` is an ordinary stat id. The
whole reading, the 201-value operator switch and the method are in
[`criteria-grammar.md`](criteria-grammar.md); `CriteriaOperator` carries the names the client states.
`AddProc`'s twin above swapping `93` and `103` reads as what it is with those names: `HasFeat` against
`HasNotFeat`.
