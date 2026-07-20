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
fixed record. The capture taxonomy (`tools/capture/decode_gameactionlist.py`) reflects this: the clean
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
- **Type id + key list: RECOVERED** (2026-07-11 live Frida dump — see the RESOLVED section below).
  `docs/re/gameaction_types.json` holds all 68 types' value orders, so any element (recast included) is
  byte-constructible. The grammar is no longer a blocker; #271's residue is an in-client base-cooldown check +
  an optional low-prio override emit.

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
