# IIR `ReadSubClass` readers via vtable-xref

Decode of the world dynels + loot bags and the movement/AI records (MoveTask, GameActionList,
BehaviorControlCenter). Companion to the FA015807 decode (`docs/re/fa015807-spawn-reader.md`); same stream
primitives.

The vtable-xref method below applies across the whole 141-class IIR catalog: every on-the-wire type is byte-exact
decoded and every binary-only/debug reader recovered — see `docs/re/chardcmove-grammar.md`,
`docs/re/iir-readers-combat-world.md`, `docs/re/iir-readers-conversation-misc.md`,
`docs/re/iir-readers-binary-only.md`. This page is the deep-dive for the dynel/loot/movetask/gameactionlist/
behaviorcontrol readers.

## The vtable-xref method (where the string anchor fails)

The FA015807 reader was found by anchoring on its `"...ReadSubClass() Received a bad stream upon entering!"`
assert. **Only `SimpleChar`/`CoreChar`/`SimpleCharKilledBy` carry that string** — the dynel/loot/movetask/etc.
readers don't, and Ghidra creates no usable xref to their `.cpp` filename string. Find them via the **vtable**
instead (`tools/ghidra/DumpVtableReaders.java`):

1. In the *known* `SimpleCharFullUpdateIIR_t::vftable`, find the slot whose pointer == its reader
   (`FUN_01121c00`). That is **vtable slot 8** = the `ReadSubClass` virtual.
2. Read slot 8 of every other `<Class>IIR*::vftable` → that class's `ReadSubClass` → decompile.

Reproduce: `DumpVtableReaders.java` then `DumpHelpers.java` (the shared reader helpers); both write to
`$FAOLAN_GHIDRA_OUT`.

## Shared stream primitives
| Decompiled fn | Meaning |
|---|---|
| `FUN_00cba1b0(dst, n)` | raw blob read of `n` bytes |
| `FUN_0048f900` → `U32` | `ReadUInt32` (4 bytes, BE-swapped per the stream's `+0x1a` endian flag) |
| `FUN_004e0390` → `U8` | `ReadByte` (1 byte) |
| `FUN_00cbfb80` → `BLOB` | **ReadLengthPrefixedBlob**: `[U32 len][len bytes]` — the embedded-protobuf reader |
| `FUN_0080c620` → `KDTREE` | the kd-tree / collision / bounding-box struct reader (see below) |
| `0x3F1` (1009) | the count-framing invariant: a list's count word is always `(count + 1) * 0x3F1` |

## Decoded readers (vtable slot 8)

### SimpleDynelFullUpdateIIR_c (`0x66AEDD50`) — `FUN_007a2510`
```
[base Read: u32 type, u32 handle, u32 0, varint-len ProtoGameCodeDynelIIR transform]
ReadSubClass:
  BLOB  -> ProtoSimpleDynelIIR protobuf      (FUN_00cbfb80; name/display_name/template_item_id/flags)
  KDTREE                                       (FUN_0080c620)
```
**Validated** by `SnapshotTailsFromScratchTests` against **all** captured dynel records (every
login capture, 100%): the `[u32 blobLen]` frames a protobuf that starts with field 1 (`0x08`, kdtree) and
carries the name field. The protobuf is the already-recovered `ProtoSimpleDynelIIR` — so the dynel is
constructible from DB rows + that proto, no bespoke decoder.

### LootBagFullUpdateIIR_c (`0xF096251E`) — `FUN_007a3db0`
```
ReadSubClass:  BLOB -> ProtoLootBagIIR ;  KDTREE ;  BLOB   (a second length-prefixed blob @ +0x16c)
```
Same shape as the dynel plus one extra trailing blob. (Beach loot bags carry session-specific contents; that is a
content question, not a grammar one.)

### MoveTaskIIR_c (`0x96C46740`) — `FUN_00a340e0`
```
[base Read: the transform / float-heavy state block]
ReadSubClass:  U8 (bool @ +0x3c) ;  U32 @ +0x30 ;  U32 @ +0x34 ;  U32 @ +0x38 ;  then resolve the move-task obj
```
The bulk of MoveTask is the base-class read; `ReadSubClass` only adds a bool + 3 u32. The class holds a
`ProtoEmptyMessage` vtable — its body is largely protobuf. That base read is fully decoded in
[movetask-grammar.md](movetask-grammar.md); the +0x30/0x34/0x38 trio is the mode-00 "approach/face" params.

### GameActionListIIR_t (`0xF508F4C1`) — `FUN_008679f0` → `FUN_00870500`
```
U32 framed = (count + 1) * 0x3F1                      # same framing as the FA015807 sub-list
count × { GameActionData blob }                        # log: "Streaming in spell list with N elements"
then: U8 bool @ +0x3c ;  U32 @ +0x40
```
This is the **spell/action list**: a `0x3F1`-framed count then one `GameActionData` blob per entry. The element
grammar and the full type-id/key catalog are in [gameactionlist-grammar.md](gameactionlist-grammar.md).

### BehaviorControlCenterIIR_c (`0x642CD3D6`) — `FUN_005e24e0`
A tagged union — the generic "Interact"/behavior channel:
```
U8 kind @ +0x1c
switch(kind):
  2,3,4,5,7,10,15,16,18 -> U32 actionId (resolve action)
  6,8,9,13,17           -> (nothing; kind only)
  11,12                 -> U32 @ +0x28
  14                    -> U32 @ +0x24 ; if !=0 -> U32 actionId (resolve)
```
The `actionId` is a key into the client's Command catalog, and kind 14's extra word is a delay in hundredths of
a second. Both, plus the per-command readers and the attack tail, are in
[behaviorcontrolcenter-grammar.md](behaviorcontrolcenter-grammar.md).

## Decode coverage
The grammars are decoded; the dynel is capture-validated 100%. MoveTask's base read is pinned in full — the
recursive grammar round-trips 18,455/18,455 captured records byte-exact and `PatrolPath`/`PatrolWaypoint` source
the waypoints ([movetask-grammar.md](movetask-grammar.md)). Same column↔offset caveat as
[fa015807-spawn-reader.md](fa015807-spawn-reader.md).
