# DB-container world-entry inventory — RESOLVED & FIXED (2026-06-13)

The player's real bag + equipment doll are delivered via the world-entry `0x737A6DF9`
(FullPlayerCharacterIIR) snapshot, with the DB containers substituted in. **Default ON, unconditional**
(`RetailLoginReplay.BuildBeachInventoryPacket`). In-client verified: chat connects, names render, the bag
shows the player's real DB items. This file records the root-cause + the fix; it is no longer a TODO.

## Root cause (the bug that broke it)
The snapshot body's protobuf root is **length-prefixed by a 4-byte BIG-ENDIAN length**, and the server was
writing that length wrong whenever it regenerated (resized) the root — so the client read the wrong number of
bytes for the root and then read the trailing-subclass version from deep inside the tails:
```
ERROR: CoreFullPlayerCharacterIIR::ReadSubClass() Wrong IIR_t version <garbage>, (current is 24)
ERROR: n3InfoItemRemote::Construct: ReadSubClass for IIR 1937403385 (FullPlayerCharacterIIR) failed!
       Identity 50000:<charId>. Blob data may be inconsistant, data invalid
```
That invalid FullChar cascades: world entry never completes → names don't render → no CommunityServer (chat).

The old model (`AdjustSnapshotPrefixRootLength`) believed the 8-byte prefix was
`[u32 base-version=2][protobuf varint = 4483 + rootLen][trailing 81 06]` and delta-adjusted that varint. That
was **byte-misaligned**: it adjusted bytes that overlap, but not the real length field, so on resize the
real BE32 length stayed ~`0x000002xx` (686→700→708) regardless of the actual root size — which is exactly why
the client always read the version at root-relative ~697 for any resized root. (`varint − rootLen = 4483` was
a coincidence of the two near-equal-length reference captures.)

## The real framing (recovered from AgeOfConan.exe via Ghidra, 2026-06-13)
`n3InfoItemRemote::Construct` (`FUN_005b9d30`) reads the IIR; the body is **all big-endian**:
```
[u32 KEY = subop = 0x737A6DF9]      # the IIR type hash (== 1937403385); looked up in the IIR registry
[u32 identity.type = 50000]          # 0xC350  -> the "Identity 50000:<id>" in the error
[u32 identity.instance]              # the char/zone instance
[u8  flag]                           # 0x00 for the beach snapshot
[u32 BE blobLen]                     # <-- THE root delimiter (FUN_00cbfb80 reads exactly this many bytes)
[protobuf blob = blobLen bytes]      # = field5 (28 81 06 = field5 varint 769) + FullCharacterIIR root
                                     #   (field 6 containers ×4 + f7 + f8)
[u32 BE version = 0x18]              # CoreFullPlayerCharacterIIR::ReadSubClass (FUN_00797440) reads this; must be 24
[tails subclass ...]                 # PFAccessWindow table, progression XML, quests, etc.
```
Read primitives: `FUN_0048f900` = ReadUInt32 (BE), `FUN_00cba1b0` = ReadBytes(n), `FUN_004e0340` = ReadByte,
`FUN_00cbfb80` = "read a length-delimited protobuf blob" (reads BE32 length, then that many bytes, then parses).

Key invariant, proven byte-exact on both beach captures (`SnapshotBlobLengthTests`):
```
blobLen == field5Len + rootLen          # strie: 686 = 3 + 683 ;  bigboy: 681 = 3 + 678
```
where `field5Len = prefix.Length - 5` (the prefix's `[flag(1)][len(4)]` header; the rest is field5 = `28 81 06`),
and `rootLen` = the regenerated FullCharacterIIR root (field-6 containers + f7/f8). The captured 8-byte prefix
`00 00 00 02 ae 28 81 06` is therefore really `[00=flag][00 00 02 ae = blobLen 686][28 81 06 = field5]`.

## The fix
`GameServerListener.SetSnapshotBlobLength(prefix, rootLength)` writes the correct BE32 length
(`field5Len + rootLength`) at prefix offset 1, preserving the flag and field5 bytes. `BuildBeachInventoryPacket`
calls it after regenerating the root (replacing the dead `AdjustSnapshotPrefixRootLength`). Unconditional —
no env flag. `AdjustSnapshotPrefixRootLength` is kept only until `SnapshotPrefixLengthTests` is migrated.

## Tests
- `Faolan.Tests/SnapshotBlobLengthTests.cs` — golden: captured prefix's BE32 == field5Len + rootLen on both
  captures; version `0x18` sits at blob end; `SetSnapshotBlobLength` round-trips and preserves flag + field5.
- `SnapshotRoundTripLosslessTests` (typed round-trip lossless), `FullCharSnapshotBuilderTests`,
  `SnapshotItemBuilderTests`, `InventorySnapshotTests` — the builder/entry layers.

## Ghidra notes (reusable)
Headless project at `/tmp/ghidra-aoc/proj` (rebuildable; the exe is stripped, no PDB). Anchor on error-string
literals and pointer-search the **C-string start's** LE32 in memory (Ghidra didn't auto-create the string
xrefs). Scripts in `/tmp/ghidra-aoc/scripts/{DumpReadSubClass,SurveyStrings}.java`. The relevant functions:
`n3InfoItemRemote::Construct = FUN_005b9d30`, `CoreFullPlayerCharacterIIR::ReadSubClass = FUN_00797440`,
protobuf-blob read = `FUN_00cbfb80`, ReadU32(BE) = `FUN_0048f900`. See `docs/re/` for the saved decompiles +
the subsystem string index.
