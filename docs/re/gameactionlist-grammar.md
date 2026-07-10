# GameActionListIIR_t (0xF508F4C1) wire grammar — a property-map action list

Recovered from `AgeOfConan.exe` (Ghidra `Captures/ghidra/output`). The "GameActionList" is the per-dynel
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
fixed record. The capture taxonomy (`Captures/scripts/decode_gameactionlist.py`) reflects this: the clean
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
- **Remaining to emit SetSpellRecastTime (#271) from scratch:** enumerate the SetSpellRecastTime **type id** and
  its descriptor **key list** (order of values — includes `0x463`, `0x2f9`) out of `FUN_008c3910` / the type
  registration + the `DAT_025073c0` stat-key table. With those, the element is fully constructible (no capture
  needed) — the grammar itself is no longer the blocker.
