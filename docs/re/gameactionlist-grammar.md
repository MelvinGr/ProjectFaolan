# GameActionListIIR_t (0xF508F4C1) wire grammar — a property-map action list

Recovered from `AgeOfConan.exe` (Ghidra `tools/ghidra/output`). The "GameActionList" is the per-dynel
**spell/action list** — a `0x3F1`-framed list of action elements, each a **keyed stat/property map** (not the
flat "header + verbatim tail" the capture-only model first suggested). Gitea #190.

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

Each action carries a DIFFERENT set of properties (an animation action has a string name property; a
positional action has numeric properties), so the element length varies (4–91 B) — it's a property bag, not a
fixed record. The capture taxonomy (`GameActionListCodecTests`) reflects this: the clean
`[u16 len][anim name]` form is one string-valued property; the rest are numeric property sets.

## Element deserialize — recovered 2026-07 (`FUN_00899a60`, #271)

The per-element reader is `FUN_00899a60` (called by the list loop in `FUN_00870500` right before the
`"Something went wrong while reading a GameActionData blob"` guard). `GameActionData` is a 0x68-byte object
whose ctor (`FUN_008985c0`) sets up **three typed property maps** — so a property's VALUE TYPE is which map its
key lands in:

- `fcVector_x<BinSearchMapKeyValue<uint,int>>`   (+0x1c) — int props
- `fcVector_x<BinSearchMapKeyValue<uint,float>>` (+0x2c) — float props
- `fcVector_x<BinSearchMapKeyValue<uint,String>>` (+0x3c) — string props (keys `0x4d9`/`0x4da`, e.g. `idle2_emote`)

`FUN_00899a60` reads (`FUN_0048f900` = read u32 from the stream). The layout is **positional, not key-tagged** —
the KEYS live in a static per-type descriptor, the wire carries only VALUES in that type's order:

```
[u32 typeId]    -> obj+0x10   (the GameActionData TYPE; looked up via FUN_008c3910)
[u32 field]     -> obj+0x14
[u32 == 4]      (validated)
[u32 subCount]  (< 1001), then subCount × { sub-record: FUN_006e9f20 + FUN_006e70e0 }
[u32 for 0x2d5][u32 for 0x2d6][u32 for 0x2f2][u32 for 0x2f5]   (four ALWAYS-present int values, positional)
--- then the TYPE-SPECIFIC values ---
descriptor = FUN_008c3910(typeId)         # unknown type -> "Tried to stream unknown GameActionData with type N"
for each entry in descriptor.keyList (at descriptor+0xc, u32 entries, until an entry == 0xFF):
    key = statKeyTable[entry & 0xFF]      # DAT_025073c0[] maps the descriptor byte -> the real stat key
    if key in {0x4d9, 0x4da}:  value = read std::string   (FUN_0048f840)   # the string props (anim names)
    else:                      value = read u32           (FUN_0048f900)
    map[key] = value
```

So the wire has **NO keys and NO terminator** — an element is `[typeId][field][4][subCount][sub-records][4 fixed
u32][type-specific values]`, and the whole per-type layout is fixed once you know the type id + its descriptor
key list. This supersedes the earlier "`[u32 statKey][value]` loop terminated by `0xFF`" sketch (the `0xFF` and
the keys are in the STATIC descriptor, not on the wire). Read at runtime by the handler via
`FUN_0089bd80(action, key)` — e.g. SetSpellRecastTime (`FUN_008b0f50`, string `GameCode/GameAction/SetSpellRecastTime`)
pulls keys `0x463` (caster/line) and `0x2f9` (recast centiseconds).

## Status (#190 / #271)

- Header/list framing fully decoded + validated 925/925; `GameActionListCodec` round-trips element bytes verbatim.
- The full element grammar is now recovered (positional, type-descriptor-driven; string vs u32 by key).
- **Type id + key list: RECOVERED for all 260 types** — statically, from the initialiser `FUN_008bdf40`
  ([`gameaction-keylists.tsv`](gameaction-keylists.tsv), 2026-08-03; the 2026-07-11 live Frida dump reached 68 of
  them and is superseded where the two disagree). Any element is byte-constructible. The grammar is no longer a
  blocker; #271's residue is an in-client base-cooldown check + an optional low-prio override emit.

## RESOLVED 2026-08-03 — the whole descriptor table IS statically recoverable, and it names the RDB modifier record (#591)

Two things the sections below got wrong, both corrected here:

1. **"Not statically recoverable" was wrong.** The populator is `FUN_008bdf40`, one flat initialiser that writes
   all 260 descriptors — name, label, key list and (on ~20 types) the key list's own **field names**. It writes
   them two ways, which is why a single-form search missed it: the address is constant-folded for most types
   (`MOV [base+8], <enumId>`), and written through a running cursor for the rest
   (`PUSH <name>; MOV ECX,<enumId>; CALL FUN_008bde20` then `MOV [ECX + EAX*4 + 0xc + 4j], <key>`).
   `tools/ghidra/DumpGameActionKeyLists.java` walks it with constant-register tracking and emits
   [`gameaction-keylists.tsv`](gameaction-keylists.tsv) — **260 types, all with key lists**, against the 68 the
   live Frida dump reached.
   ⚠️ Where it disagrees with [`gameaction_types.json`](gameaction_types.json), **the static reading is the right
   one**: `AddProc` and `AddSpellProc` have SIX keys there, not four, and the RDB record widths (below) confirm six.
   The descriptor is `0xF4` bytes, not the `0x41` the live dump inferred: key list at `+0x0c`, a parallel field-name
   array at `+0x5c`, a third (always zero) at `+0xac`.
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

⚠️ **That measurement is an upper BOUND, not the width** — `recordLength - elementStart` also swallows whatever
the record carries after the element list, which is why an exact match on 17 kinds is the strong result and a
surplus is not automatically a disagreement. `--report feat-effect-widths` prints the surplus words: `Modify`'s
wide variants are followed by a pair of list frames (`0x7E2`, `0x3F1`), and `AddSkinnedMesh` (measured 12 words,
predicted 9) by `2 9 207912`.

### Widths measured by TILING — exact, on 27 kinds (#595)

The bound above only exists because a single-element record has no end marker but its own. Elements sit **back to
back with nothing between them**, so a record holding SEVERAL measures the non-final ones *exactly*: the distance
from an element's opening type word to the next element's IS its width. The same report now makes that second,
independent measurement over 15,987 element regions, tallying a width only where the region's own `0x3F1` frame
count agrees with the elements the signature walk found (15,880 of them do) — agreement being the record's own
cheap proof that two elements really are adjacent rather than separated by trailing content.

It reaches **27 kinds against the ceiling pass's 17**, all exact, and settles both of that pass's open readings:

- `AddSkinnedMesh` tiles at exactly **9 words**, so its 12-word ceiling was trailing record content — *not* the
  string-valued key it looked like.
- `Charge` never occurs as a non-final element, so its lone 8-word reading stays what it was: probably a
  coincidental signature hit rather than an element.

### The width model has ONE exception: a TEXT slot

`8 + len(keyList)` describes an element whose every slot is a u32. **Keys `209` and `210` (`EventString`) are
text**, written `[u16 byteLength][bytes]`, which makes any element carrying one variable-width — and variable by
an ODD number of bytes, so the payload has to be WALKED slot by slot rather than indexed. `SetMorphValue`'s
"ASCII mesh name after the element" was this slot *inside* it all along.

**The set is closed, and read out of the client** `[V1: tools/ghidra/DecompileFns.java]`. The deserializer
`FUN_00899a60` takes the string branch for exactly two `GameActionStat_e` ids, `0x4d9` and `0x4da`; the key-index
table it reads (`&DAT_025073c0`, built by `FUN_00897bd0`, 244 of its 255 slots written as constants) maps index
**209 → `0x4d9`** and **210 → `0x4da`**, and nothing else to either. So a type is variable-width iff its key list
contains 209 or 210 — `GenerateName` and `NewTextEffect` are the only two carrying both.

**The length word is a u16, not a u32.** Same source: the string read is `FUN_0048f840` → `FUN_00cbf840` mode 0,
which reads a u16 and treats `>= 0x8000` as an escape into a long form no shipped template uses. The records
agree — of the 880 text-carrying elements in the feat and spell catalogs, **653 tile EXACTLY** under it (the
distance to the next element's type word equals `32 + 4×fixedSlots + 2 + len`), and the strings come out whole
with the slots after them aligned:

| element | payload, by key |
|---|---|
| `SetMorphValue` in spell 4609400 | `209="arm" 39=-10` — 41 B |
| `StartParticleEffect` in spell 3832933 | `209="spell_curseofflies" …` — the 0x12-byte slot is exactly the name |
| `StartServerSideScryScriptWithParams` in spell 325651 | `39=227065 209="spawnHash=NSAO;pointCost=1;petBuff=783738;spellID=325651"` |

None of them carries a NUL inside the counted length.

`GameActionTypes.TextKeys`, `GameActionType.IsFixedWidth` and `ItemTemplateStatModifiers.ParseElements` implement
this; `--report feat-effect-kinds` prints the audit, bucketing every text-carrying element by which length word
makes it tile to its measured width.

⚠️ **Six elements do not tile under it**, all of them `HeadText` (53057), which reads cleanly as
`[u32 7]["Dimach\0"][u32 3]` — a u32 length, the NUL counted — and measures two bytes wider than the u16 form
allows. That is one kind out of the eleven that carry text and is not modelled; the walk stops at such a slot
rather than guessing, so the element is still located and its earlier slots still read. Open thread on Gitea #596,
together with the unregistered kind 53044, which carries a string under a key list the client does not register.

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
  as padding. Also Gitea #596.

Neither is reachable as a stat modifier: the importer rejects both on general rules (a magnitude that is a
registered GameAction type id was never ours; a stat outside the id range likewise), not by name.

**Key indices that carry a name** (recovered from the types that name their own slots; a name transfers by KEY,
so it resolves that slot on every type that uses it): `0` Stat, `11` Radius, `17..20` AudioEffect*, `24`
SkillType, `25` TimedLength, `28..30` Rel{X,Y,Z}Pos, `72` TimeExist, `73` Flags, `77` Hash, `84` Level, `103`
NPCMaster, `174` Substate, `175` Amount, `209` EventString, `1305` FloatValue. Key `39` is deliberately left
blank: it is `Value` on a modifier and `ScriptID` on a script action, so it carries no transferable name. Key `1`
is unnamed but is the **spell line** by position — it is the slot that distinguishes every `…Line…` type from its
per-spell twin.

### A key index is not the property id — it indexes into `GameActionStat_e`

A key-list entry is an INDEX, not the property id the client stores under. `FUN_014935c0(byte keyIndex)` maps one
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
| 172 | 1204 (`0x4b4`) | ramp low LEVEL |
| 173 | 1205 (`0x4b5`) | ramp high LEVEL |
| 174 | 1206 (`0x4b6`) | ramp low VALUE (the client calls it `Substate`) |
| 175 | 1207 (`0x4b7`) | Amount — and the ramp's high VALUE |

Indices 172…174 are **derived**, not read out of the table: the interpolation code below reads exactly
`{0x4b4, 0x4b5, 0x4b6, 0x4b7}` for a type whose key list is exactly `{0, 174, 172, 175, 173}`, so the four map onto
each other; `175 → 0x4b7` is already known, `0x4b6` is the lerp's low endpoint and holds a magnitude (which of the
remaining three is the second magnitude is settled by the shipped values), and `0x4b4 < 0x4b5` matches
`172 < 173` in every record. Verify before leaning on it for a type outside this family.

⚠️ **The `GameActionStat_e` id is NOT an LDB-615 stat id**, however much it looks like one — 722…1269 lands inside
that table's id range and one slot even collides with a real name (`84` → 1119 → "PvP Cold Invulnerability
Rating"), which makes "just look the id up in LDB 615" a tempting and wrong way to name the remaining unnamed
slots (`55`, `66`, `67`, `117`, `236`, `237`). What LDB 615 names is the VALUE stored under key `0`, not the key.

### What `Value` (39) and `Amount` (175) mean — from the tooltip builder

`Misc\ItemInfoCreator.cpp` renders a `SpellStatModifier` / `SpellLineStatModifier` / `0xD023` element at
`014a25a6`, reading exactly three of its four slots — `GetIntStat` on `0x2d2`, `0x2f9`, `0x4b7`, i.e. Stat, Value,
Amount — and branching on `Value == 0`:

- **`Value` set** → the generic stat-delta formatter `FUN_0149a300`, which appends the stat's OWN unit suffix
  (`FUN_014a6240(stat)`) and divides by a per-stat display scale. So `Value` is an **absolute** delta in the
  stat's units.
- **`Value == 0`** → an inlined copy of the same routine applied to `Amount`, but with the suffix replaced by the
  **hardcoded `"%"`** at `0x01fbcf18` — on all three of that branch's print paths (`014a27e8`, `014a2888`, and the
  `CastingTimeSeconds` path at `014a267a`, which prints `abs(amount) * 0.01` under `"%.1f%%"`). So `Amount` is a
  **percentage**, in hundredths of a percent.

The stat id itself is resolved through `GetLocalisedString(615, stat)` — `PUSH 0x267` at `014a2714` — which is
what ties this word to the ordinary wire stat space (`CharacterStatId`) rather than a spell-local one.

Key `55` (`ChangeHitPercent` / `ChangeLineHitPercent`, values 1 or 2) is read ONLY by the element comparator
`FUN_014a5670`, never by the tooltip: it participates in sorting/grouping and carries no rendered magnitude, so
kind 53075's shipped `(line, percent)` decode does not depend on it.

### The INTERPOLATED family is a LEVEL RAMP — keys `174`/`172`/`175`/`173`

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
effective-LEVEL stats — so the element reads:

> modify `Stat` by a value ramped linearly from `174` at level `172` to `175` at level `173`, clamped at both ends.

The shipped data agrees independently and could not by chance: across all 341 feat and 376 spell elements, keys
`172`/`173` only ever hold `10`/`20`/`60` and `80`/`100`, and consecutive RANKS of one spell tile the level line
without gaps or overlaps while their values stay continuous across the seam — `Battle Cry: Marshal the Ranks`
runs `26@10 → 42@24`, `44@25 → 60@39`, `62@40 → 78@54`, `80@55 → 96@69`
(`--report feat-effect-kinds --report-arg 1040005/53220`).

This is why an element's magnitude cannot be taken positionally: word 1 is the ramp's LOW endpoint here and the
whole magnitude on `Modify`. `ItemTemplateStatModifiers.ParseStatModifiers` reads by key and returns the whole
ramp beside that endpoint (`Modifier.Ramp`); `LevelRamp.ValueAt` is the lerp above, and `FeatEffect` /
`SpellStatEffect` carry the other three slots as nullable columns.

### The mode word `element[0x40]` is NOT in the template

`FUN_008c3970` picks between stat `150` and stat `965` on `element[0x40]`, and that word is **not a slot of the
record** — it is word 0 of a **9-word invocation context** the GameAction is constructed with. `GameActionBase`'s
constructor `FUN_0089b390` block-copies those nine words into the live object at `+0x40`…`+0x60`
(`for (iVar6 = 9; ...) *puVar8 = *puVar7;` with `puVar8 = param_1 + 0x10`, i.e. byte offset `0x40`), and the RDB
deserializer `FUN_00899a60` never writes them: everything it reads goes into the key-addressed maps at `+0x1c` /
`+0x2c` / `+0x3c`, plus the four always-present ids `0x2d5`/`0x2d6`/`0x2f2`/`0x2f5`.

The word is generic, not interpolation-specific — the plain `ModifySkill` apply `FUN_0089dfd0` reads the same
`[+0x40]` through the **same** `1→3, 3→2, 4→1, 5→4` switch before calling the stat sink `FUN_007186a0`, and it is
mode `1` alone that takes the `GetOnCasterModifyPercent` / `GetOnTargetModifyPercent` branch. So mode 1 is the
CAST path, which is why it is the mode that ramps over `Casting Level` while every other mode uses `Apprentice
Level`. Both resolve to the character's level on a server that does not model mentoring, so there is nothing to
import and nothing to store.

## RE session 2026-07-10: element wire structure confirmed; both lookup tables are RUNTIME-built

Decompiled the actual (de)serializer + tables (scripts `tools/ghidra/DumpRecastDescriptor.java`,
`DumpDescriptorInit.java`; `DecompileFns` on `FUN_00899a60`/`FUN_008c3910`/`FUN_008b0f50`/`FUN_00897bd0`).

**Element wire layout — confirmed byte-for-byte from the deserializer `FUN_00899a60`:**
```
[u32 typeId]            # obj+0x10; must be 0xcf09..0xd026 (e_Type_GC_GameAction .. _End)
[u32 field]            # obj+0x14
[u32 == 4]             # version, validated (else "Invalid GameAction data version")
[u32 subCount]         # < 1001; then subCount × { FUN_006e9f20 + FUN_006e70e0 } sub-records
[u32 v(0x2d5)][u32 v(0x2d6)][u32 v(0x2f2)][u32 v(0x2f5)]   # four ALWAYS-present positional int values
--- then per the type's descriptor keyList (each entry a u32, low byte = index, list ends at u32 == 0xFF):
    idx = entry & 0xFF ; key = statKeyTable[idx]
    key in {0x4d9,0x4da} -> std::string value ; else -> u32 value
```
Cursor stride is 4 (`local_114 += 4`), terminator is the full u32 `== 0xFF`.

**`FUN_008c3910` (descriptor lookup):** `descriptor = &DAT_025077c8 + (typeId-0xcf09)*0x41` dwords → base
`0x025077c8`, stride `0x104` bytes; keyList at `descriptor+0xc`; `descriptor+8 == 0` ⇒ "unknown type".

**stat-key table `DAT_025073c0` — recovered from its runtime initializer `FUN_00897bd0`:** a linear map,
`statKeyTable[i] = 0x2d2 + i` for the low range, so the two keys the handler reads are at
**`statKeyTable[0x27] = 0x2f9`** (recast, centiseconds) and **`statKeyTable[0x58] = 0x463`** (spell id). So the
recast type's descriptor keyList must contain the byte indices `0x27` and `0x58`.

**BLOCKER (new, corrects the assumption above):** BOTH `DAT_025073c0` (stat-key table) AND `DAT_025077c8` (the
descriptor table itself) are **zero in the static exe** — runtime-built (`FUN_00897bd0` lazily fills the stat-key
table on first use; the descriptor table is populated per-type from each GameAction class's own registration
data). `FUN_00899440` is the serializer and `FUN_00898890` a by-name accessor — both CONSUME the runtime
descriptor; neither is the populator, which is per-class and not yet located. So the recast **typeId + keyList
cannot be read statically** from the descriptor table.

**Cleanest way to finish:** a LIVE memory dump of the running client (gdb/frida attach, no ASLR so Ghidra
addresses == runtime — see the live-debug memory) reading `DAT_025073c0` + walking `DAT_025077c8` for the type
whose keyList contains indices `0x27`+`0x58` → that slot's index gives the recast typeId and its full keyList
order. Alternatively keep tracing the per-class registration statically, or capture a real retail recast cast.

### Static registration path EXHAUSTED (2026-07-10, second RE session)

Chased the per-class registration to confirm whether the typeId+keyList can be read statically. It cannot:
- **Descriptor registrar = `FUN_008bde20`** — writes `descriptor+8 = typeId` and stashes the descriptor ptr in
  `DAT_0297d418` (a "currently-registering" global); a follow-up appends the keyList at `descriptor+0xc`. It has
  60+ callers (one per GameAction type).
- **SetSpellRecastTime class:** handler `FUN_008b0f50` sits in the vtable at slot `0x01fd7aac`; vtable base
  `0x01fd7a88` is installed by ctor **`FUN_008b0e10`** (thin — forwards to the shared base ctor `FUN_0089b390`
  with the type-specific args).
- **Dead end:** `FUN_008b0e10` has **zero** references — not a call, not a data pointer anywhere in the image
  (searched memory for its little-endian address: 0 hits). The GameAction objects are created via an
  inlined/data-driven factory Ghidra can't resolve, so the recast typeId + keyList are **not statically
  recoverable**. The runtime tables (`DAT_025077c8` / `DAT_025073c0`) are the only source ⇒ a **live client
  memory dump is the sole remaining path**. Scripts: `tools/ghidra/DumpRecastReg.java`, `DumpRecastDescriptor.java`.

## RESOLVED 2026-07-11 — live Frida dump of the runtime descriptor table (#271)

Attached Frida to the running client (frida-server-under-Wine, no ASLR ⇒ Ghidra VAs == runtime — see the
`frida_under_wine_client_re` memory) and read the two runtime tables directly. Scripts committed:
`tools/frida/dump_gameaction_types.py` (walks the descriptor table) + `dump_gameaction_names.py` (resolves
each descriptor's name pointer). Full catalog: **`docs/re/gameaction_types.json`** (68 named types).

**Descriptor layout — confirmed from live memory** (stride corrected: **`0x41` BYTES per slot**, not `0x104`;
consecutive named types' `descriptor+8` enum ids increment by exactly 1, which only holds at a 0x41-byte stride):
```
descriptor = 0x025077c8 + (wireTypeId - 0xcf09) * 0x41        # wireTypeId is the SLOT id (0xcf09-based)
  +0x00  char*  name        # "e_Type_GC_GameAction_<Name>"
  +0x08  u32    enumId       # compact GC enum (0xcf0a..0xcf50); the SLOT id != this
  +0x0c  u32[]  keyList      # low byte of each entry = index into statKeyTable; ends at entry == 0xFF
```
`statKeyTable` (`DAT_025073c0`) read live = the linear map `[i] = 0x2d2 + i` (confirmed indices 0..255).

**The 68 real GameAction types (enumId 0xcf0a..0xcf50) with their wire-value order** are in the JSON. The
recast-relevant ones:

| wireTypeId | name | keyList (value order after the 4 fixed ints) |
|---|---|---|
| `0xcf51` | `e_Type_GC_GameAction_Spell` | `0x2f9` (recast) |
| `0xcf81` | `e_Type_GC_GameAction_ChangeRecastTime` | `0x4c3`, `0x2f9` |
| `0xd019` | `e_Type_GC_GameAction_ChangeLineRecastTime` | `0x2d3`(line), `0x2f9` |
| `0xcfdd` | `e_Type_GC_GameAction_SpellStatModifier` | `0x463`(spell), `0x2d2`, `0x2f9`, `0x4b7` |
| `0xd009` | `e_Type_GC_GameAction_CoreSpellStatModifier` | `0x463`(spell), `0x2d2`, `0x2f9`, `0x4b7`, `0x2eb` |

The handler `FUN_008b0f50` (log `GameCode/GameAction/SetSpellRecastTime`) reads spell `0x463` + recast `0x2f9`;
it is the `Apply` of the SpellStatModifier family (`0xcfdd`/`0xd009` — the only slots carrying *both* keys; the
vtable at `0x1fd7aac` is non-RTTI so the C++ class name isn't recoverable, but the keyList match is decisive).
A direct per-spell recast override is `ChangeRecastTime` (`0xcf81`); per-line is `ChangeLineRecastTime` (`0xd019`).

**Construction is now fully specified** — the wire is positional and every type's value order is known, so any
GameAction element is byte-constructible from the grammar above + the JSON. The element is
`[wireTypeId][field][4][subCount][sub-records][0x2d5,0x2d6,0x2f2,0x2f5 ints][keyList values]`, string for keys
`0x4d9`/`0x4da` else u32. This removes the "typeId + keyList unknown" blocker permanently for `GameActionListIIR`
construction (#190, #271).

**Not shipping a recast builder yet, deliberately:** the capture set still contains no recast GameAction, and
the gameplay-capture analysis (#271 comment 2026-06-22) showed retail does **not** push a recast on a normal
cast — base cooldowns are client-side (the client reads recast from its own RDB on cast). So the remaining
`#271` scope is (a) an in-client confirmation that a granted spell goes on cooldown with no server emit, and
(b) an *optional*, low-priority server override emit (cooldown resets / AA reductions) — now constructible via
`ChangeRecastTime` the moment it's wanted.

## The full type-id catalog — 260 types, and the id space the client asserts

[`gameaction-type-ids.tsv`](gameaction-type-ids.tsv) lists every GameAction type the client registers. It extends
the 68-entry `gameaction_types.json` in COVERAGE only — the ids are that file's `enumId`, which it agrees with on
all 68 shared names. (⚠️ That file's **`typeId` column is not a wire id** — see the stride trap below.)

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

`FUN_008bdf40` initialises the table, writing the name pointer at `descriptor+0` and **the id at `descriptor+8`**.
Reading the id from `descriptor+8` and deriving it from the address both give the same answer for all 171 inline
registrations; the other 89 register via `CALL FUN_008bde20`, which passes the id in `ECX`.

### ⚠️ The stride trap — `0x104`, not `0x41`

`tools/ghidra/DumpGameActionTypes.java` used a stride of `0x41`. The real stride is `0x104`, and because
**`0x104 == 4 * 0x41`** a wrong `0x41` index still divides evenly — so "the address lands exactly on a descriptor"
is worth nothing as a check, and every id derived that way comes out **four times too far up the table**. That is
also exactly what the JSON's `typeId` column is: `53005 + 4*(enumId - 53002)`.

This was not caught by review. `StartFlymode` came out as `0xD12D` instead of `0xCF92`, sailed past a
"cross-checked, 0 mismatches" comparison (both sides shared the same 4x error), and **crashed the client** when
sent (2026-07-29). Only the range assert caught it. The lesson: for a table index, validate against the client's
own BOUNDS, never against another derivation of the same quantity.

⚠️ **KeyLists are still NOT recoverable statically.** The descriptor table is BSS — filled at runtime — so a read
of `descriptor+0xc` returns zeros; the JSON's key lists come from a live Frida dump. A type in this TSV but not in
the JSON has an **unknown parameter list**, and assuming an empty one is a guess to be proven in-client.
