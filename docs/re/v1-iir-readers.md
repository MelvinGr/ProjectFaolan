# V1 IIR wire grammars — recovered from the client readers (2026-07)

The 11 IIR types the coverage matrix flagged **V1** ("nothing builds or parses it") are all s2c types the client
parses via its `ReadSubClass` (vtable slot 8). Recovered them without captures via the vtable-xref method
(`tools/ghidra/DumpV1IirReaders.java` → `tools/ghidra/output/v1_readers.txt`), then validated
each against the captured body lengths. `FUN_0048f900` = read u32; `FUN_0048f720` = read u64; `FUN_0048f840` =
read `std::string`. Body = the carrier `[len][subop][0xC350][handle][env]` then the reader bytes below.

| IIR | opcode | reader | grammar | capture check |
|---|---|---|---|---|
| **RegenIIR** | `0x28D0AC99` | `FUN_0079dea0` | `[i32 hpDelta][i32 manaDelta][i32 staminaDelta]` — DECODED: the apply method `FUN_0079dd10` ADDS each to a CURRENT vital (health 0x1B, mana 0x1FA, stamina 0x1F8), ×100 wire scale. Retail's per-tick regen channel | 1045/1045 = 12 B ✓ |
| **LockedToChangedIIR** | `0x6B0AEC58` | `FUN_007a0630` | `[u32 a][u32 b]` (target-lock pair) | 78/78 = 8 B ✓ |
| **ChainKillIIR** | `0x0EED7068` | `FUN_0079c0b0` | `[u32]` | 30/30 = 4 B ✓ |
| **FeedbackIIR** | `0xBA1FD222` | `FUN_0079f8e0` | `[u32][u32][u32]` + uses the env byte | 21/21 = 13 B (3×u32 + 1) |
| **CrimeIIR** | `0xC7954504` | `FUN_0148c9f0` | env-gated: `[u32]` **iff** env==1, else empty | 3/3 = 5 B (env==1) ✓ |
| **SpellBookIIR** | `0x6BA10936` | `FUN_00795ba0` | `[u32]` (triggers a spellbook refresh on the handle) | 8/8 = 4 B ✓ |
| **KnubotFinishTradeIIR** | `0x7446F3FA` | `FUN_009a7c20` | base-prefix + `[u32 flag][u64 id]` | 3/3 = 22 B (base reads 10) |
| **KnubotStartTradeIIR** | `0x4B2A47BF` | `FUN_009a63a0` | `[u64][u32 count][ count × { std::string name, u32, u32, u32, u32 } ][std::string …]` — the **shop item list** | 1676–1772 B (variable) ✓ |
| **AreaFormulaIIR** | `0xCE30BE4E` | `FUN_0079e330` | `[0x3F1-list {u32 class, u32 instance}][u32][0x3F1-list {float x, y, z}][u32]` — an entity ref + a world **position** (the AoE centre); `0x3F1` list framing à la GameActionList | 28–52 B (variable) ✓ |
| **TemplateActionIIR** | `0x6C300937` | `FUN_007a0b00` | `[item-template struct (FUN_005d5150)][u32 × 6]` — the struct embeds item **keys** (the `B2 90 80 5C 29 E6 27 CA` equip-identity pattern + TEA-key-like blocks), so it's an item record, not a flat struct | 10/10 = 92 B ✓ |
| **UpdateInventoryIIR** | `0xA74D2814` | `FUN_005b9230` (stub) | the body is **protobuf** — repeated `ProtoAcgItem` entries, each carrying **3× `ProtoDecryptionKey`** (the item's 4×fixed32 TEA keys, `0d/15/1d/25`-tagged; the `0a14/1214/1a14` sub-messages). Same item schema as the snapshot (`ProtoInventoryGame`). The slot-8 reader is a `return 0` stub because the client parses it as protobuf, not fixed fields — reuse `ProtoInventoryGame`/`ProtoAcgItem`, don't hand-roll | 25–1137 B protobuf ✓ |

## Notes

- **RegenIIR** is by far the most frequent (1045 instances) — it IS retail's live regen channel. DECODED
  (2026-07-22, `FUN_0079dd10`): the three fields are per-tick `[+health][+mana][+stamina]` deltas (×100 wire
  scale, signed - can drain) that the client ADDS to the current vitals (stats 0x1B/0x1FA/0x1F8). So retail
  regenerates the bars smoothly with a compact 12-byte delta instead of pushing a full 0x96B8DC59 stat block;
  `RegenCodec` now models this and the `WorldSimulation` regen can emit it (delta = the per-tick rise).
- **KnubotStartTradeIIR** is the vendor/shop item list (per-item: name + four u32s = template/price/stock/flags,
  TBD). It's constructible once those four fields are named. (Our shops already open from the captured burst; this
  is the from-scratch path.)
- **CrimeIIR** carries its single u32 only when the carrier env byte == 1 — a conditional field, not a fixed body.
- **SpellBookIIR** / **UpdateInventoryIIR** read little/nothing in the subclass — they're near-trigger markers
  (the client acts on the carrier handle); `UpdateInventoryIIR`'s 12-B captures vs the stub reader is the one
  open discrepancy (the real reader may be a base-class method, not the slot-8 override).
- **AreaFormulaIIR** reuses the `0x3F1` list framing (same invariant as `GameActionListIIR`,
  `docs/re/gameactionlist-grammar.md`).

## Status

Grammar decoded + a byte-exact codec with a captured round-trip for **all eleven** (`V1IirCodecTests`) — the IIR
coverage matrix is now **0 V1** (48 V3 / 27 V2). Codecs: `RegenCodec`, `LockedToChangedCodec`, `ChainKillCodec`,
`SpellBookCodec`, `FeedbackIirCodec`, `CrimeCodec`, `UpdateInventoryCodec` (frames `[handle][env][u32 pbLen]
[ProtoInventoryGame protobuf]`), `TemplateActionCodec` (`[68-B item struct][u32×6]`), `KnubotFinishTradeCodec`,
`AreaFormulaCodec` (the two `0x3F1` ref+position lists, fully modelled), and `KnubotStartTradeCodec`.

Two carry an inner structure verbatim rather than re-modelling an existing subsystem: `UpdateInventoryCodec`
carries the item **protobuf** (deserialize with `ProtoInventoryGame`/`ProtoAcgItem`), and `KnubotStartTradeCodec`
carries the Knubot **XML `<remoteformat>` shop dialogue** payload (the conversation-packet grammar) — both
byte-exact, both with the frame constructed. `TemplateActionCodec` carries its 68-B item-identity struct verbatim
(name + item keys) with the six trailing u32 typed. The rest are fully field-typed.
