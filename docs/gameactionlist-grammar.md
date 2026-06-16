# GameActionListIIR_t (0xF508F4C1) wire grammar — a property-map action list

Recovered from `AgeOfConan.exe` (Ghidra `/tmp/ghidra-aoc/proj`). The "GameActionList" is the per-dynel
**spell/action list** — a `0x3F1`-framed list of action elements, each a **keyed stat/property map** (not the
flat "header + verbatim tail" the capture-only model first suggested). Gitea #190.

## Reader chain

- `GameActionListIIR_t::ReadSubClass` (`FUN_008679f0`): calls the list reader `FUN_00870500`, then reads
  `[u8 flag]` (+0x3c) + `[u32]` (+0x40).
- **List** `FUN_00870500`: `[u32 c]` with the **`0x3F1` invariant** (`c % 0x3F1 == 0 && c > 0`, element count
  `= c/0x3F1 - 1`; asserts "Streaming in spell list with N elements"). So the `0x000007E2` seen in captures is
  `2 × 0x3F1` ⇒ **1 element**. Then `count` elements, each a 0x68-byte action object (`FUN_008985c0` etc.).
- **Element** `FUN_008985c0`: reads `[u32][u32]` ids, then a **loop of `[u32 statKey][value]`** property entries
  (keys looked up in a BinSearchMap; `0x2d5/0x2d6/0x2f2/0x2f5` are well-known keys), terminated by key `0xFF`.
  The **value type depends on the key**: keys `0x4d9`/`0x4da` read a `std::string` (`FUN_0048f840`, e.g. the
  animation name `idle2_emote` / `movement_*_idle`); other keys read a `u32`/float.

## Why the bodies looked multi-form

Each action carries a DIFFERENT set of properties (an animation action has a string name property; a
positional action has numeric properties), so the element length varies (4–91 B) — it's a property bag, not a
fixed record. The capture taxonomy (`Captures/decode_gameactionlist.py`) reflects this: the clean
`[u16 len][anim name]` form is one string-valued property; the rest are numeric property sets.

## Status (#190)

- Header/list framing fully decoded + validated 570/570; `GameActionListCodec` round-trips the element bytes
  verbatim (byte-exact).
- Decoding to a CONSTRUCTIVE property-map codec needs the full **statKey → value-type table** (which keys are
  string / float / u32 / the `0xFF` terminator) — a large enumeration to harvest from `FUN_008985c0`. The
  grammar SHAPE (0x3F1 list + keyed property map + string keys 0x4d9/0x4da) is decoded; the key table is the
  remaining enumeration.
