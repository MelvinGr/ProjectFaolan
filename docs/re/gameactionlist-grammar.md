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
  static per-type descriptor; string values for keys `0x4d9`/`0x4da`, u32 otherwise).

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

⚠️ **The measurement is an upper BOUND, not the width** — `recordLength - elementStart` also swallows whatever
the record carries after the element list, which is why an exact match on 17 kinds is the strong result and a
surplus is not automatically a disagreement. `--report feat-effect-widths` now prints the surplus words, and both
that occur are trailing RECORD content rather than element bytes: `AddSkinnedMesh` (measured 12 words, predicted
9) is followed by `2 9 207912`, and `SetMorphValue` by an ASCII mesh name — while `Modify`'s wide variants are
followed by a pair of list frames (`0x7E2`, `0x3F1`). The element width itself is confirmed independently by
MULTI-element records, where the elements sit back to back with nothing between them: feat 217728 carries four
`AddSkinnedMesh` elements at exactly 9 words each.

The one real miss is `Charge` — predicted 13 words, measured 8, i.e. an element with NO payload words at all. It
is a single record and is more likely a coincidental prefix hit than a real element.

**Key indices that carry a name** (recovered from the types that name their own slots; a name transfers by KEY,
so it resolves that slot on every type that uses it): `0` Stat, `11` Radius, `17..20` AudioEffect*, `24`
SkillType, `25` TimedLength, `28..30` Rel{X,Y,Z}Pos, `72` TimeExist, `73` Flags, `77` Hash, `84` Level, `103`
NPCMaster, `174` Substate, `175` Amount, `209` EventString, `1305` FloatValue. Key `39` is deliberately left
blank: it is `Value` on a modifier and `ScriptID` on a script action, so it carries no transferable name. Key `1`
is unnamed but is the **spell line** by position — it is the slot that distinguishes every `…Line…` type from its
per-spell twin.

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
