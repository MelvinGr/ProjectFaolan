# DB-container world-entry inventory — handoff (2026-06-13)

Paste-ready prompt + state of play for **re-enabling the DB-container world-entry snapshot** (the player's
real bag + equipment doll at login) without breaking world entry / chat. It is currently **default OFF**
because the rebuilt snapshot is rejected by the client; the verbatim snapshot is the safe default.

## Symptom (why it's disabled)
With DB containers ON, the client REJECTS the player's own world-entry `0x737A6DF9` snapshot. In
`AgeOfConan.log` (ignore the surrounding RDB/resource/AnimSys noise):

```
ERROR: N3.N3InfoItemRemote - n3InfoItemRemote::Construct: ReadSubClass for IIR 1937403385
       (FullPlayerCharacterIIR) failed! Identity 50000:<charId>. Blob data may be inconsistant, data invalid
```

`1937403385 = 0x737A6DF9`. Because the client can't construct the player's FullChar, world entry never
fully completes → **character names don't render and the client never connects to the CommunityServer
(chat, port 7012)**. This is what broke chat this session (fixed by defaulting to the verbatim snapshot,
commit `5c7f2a8`). The VERBATIM snapshot is byte-valid and IIR-error-free.

## The key fact
This path **worked in-client on 2026-06-12** (commit `20f2cc1`: bag + equipment doll rendered, chat
connected). It broke after this session's world-entry rework. So the builder's design is sound — a
world-entry change since then desynced the **rebuilt root** from the **prefix root-length varint** the
client uses to delimit the protobuf root. Find that desync; don't rewrite the builder.

## 2026-06-13 RESOLVED DIAGNOSIS — the root CANNOT be resized with the framing as decoded; needs Ghidra
In-client testing this session (3 runs, Darktemplarchar on the beach) **definitively** locates the fault:
**any root resize misframes the trailing subclass; the builder/adjust/round-trip are all sound.** The
`varint − rootLen = 4483` model fits the verbatim capture but does NOT describe how the client actually
finds the root/tail boundary — so DB containers can't ship until that boundary logic is recovered from the
client exe.

### What was ruled out (sound)
- Code is byte-identical to `20f2cc1`: `git diff 20f2cc1..HEAD` over
  `Faolan/GameServer/Packets/FullCharSnapshot/` is **empty**; `BuildBeachInventoryPacket` /
  `AdjustSnapshotPrefixRootLength` / `SendWorldEntry` differ only by the env default flip. So today's rebuilt
  bytes == the bytes from when it allegedly worked.
- The typed root round-trip is **byte-lossless** (`Faolan.Tests/SnapshotRoundTripLosslessTests.cs`, both
  captures) — disproves the dropped-`0x18`-version-byte theory (step 4); protobuf-net keeps unknown fields.
- A single built entry is byte-exact vs the captured Slave Manacles (`SnapshotItemBuilderTests`); empty
  containers are byte-identical to the capture; both the full (313 B) and empty (65 B) regen roots **walk
  cleanly** as protobuf, ending `f7=0,f8=1` with no over-run. Top-level root layout matches verbatim
  (`f6×4, f7, f8`). The captured tails and `BeachTailsBuilder` scratch tails are **byte-identical**.

### The in-client evidence (the real fault: root/tail boundary)
With `FAOLAN_WORLDENTRY_DB_CONTAINERS=1`, the client always fails the player's snapshot:
`CoreFullPlayerCharacterIIR::ReadSubClass() Wrong IIR_t version <garbage>, (current is 24)` →
`ReadSubClass for IIR 1937403385 (FullPlayerCharacterIIR) failed`. The trailing-subclass version (24 = `0x18`,
which sits at **tails offset 0**, i.e. immediately after the root) is read from the WRONG place:
- **verbatim** root 683 B → version read at root-relative **683** (= root_end, tails[0]) → **24, OK**.
- **regen** root 313 B → reads `0x3022206b` (XML text `0" k`), which is at tails offsets {277,**384**,491,840}
  → root-relative {590,**697**,804,1153}.
- **empty** root 65 B → reads `0xA8000000`, at tails offsets {**632**,640,6784} → root-relative {**697**,705,6849}.

**Root-relative 697 is common to both resized roots** but verbatim reads at 683 — i.e. the client's
read point does NOT move to our new root_end and does NOT equal `V−4483` (which would be 313 / 65). It is
≈ fixed ~697 for regenerated roots regardless of the length we encode. Conclusion: scratch prefix +
scratch tails did **not** help (root 313 still rejected, bag empty); the client derives the boundary by some
mechanism the prefix varint alone doesn't drive. Reproduced both with delta-adjust+captured-tails AND with
`BeachSnapshotPrefix.Build`+`BeachTailsBuilder` — same failure.

The "worked on 2026-06-12 (16/16 smoke)" claim was the **post-load** scratch path (which the client
ignores/rejects for an already-built identity) and/or never resized the root meaningfully; the world-entry
path almost certainly never delivered a *resized* bag in-client. The equipment **doll** that did render comes
from the entity SPAWN (`PlayerSpawnEquipment`), a separate, still-working path — not this snapshot.

### Next step (do this, don't re-run the offset guesswork)
Recover the boundary algorithm from the client: Ghidra on `CoreFullPlayerCharacterIIR::ReadSubClass` (and the
`N3InfoItemRemote::Construct` caller) to see exactly how it computes where the protobuf root ends / where it
reads the next `IIR_t` version (24). The two data points above (683→683, 313→~697, 65→~697) don't yield a
clean linear formula, so the math must come from the code. Until then the **verbatim snapshot stays the
default** (chat + names work); `FAOLAN_WORLDENTRY_DB_CONTAINERS=1` remains opt-in and broken. Steps 1–4 below
are the original (now-superseded) static plan, kept for reference.

## How the snapshot is framed (the crux)
`0x737A6DF9` body = `[subop][c350][instance][8-byte IIR prefix][protobuf root = FullCharacterIIR][tails]`.
- The client delimits the root by a **root-length varint at prefix offset 4**. Prefix =
  `00 00 00 02` (base ver 2) + varint + `81 06`. Proven across captures: `varint − rootLen = 4483`
  (the varint tracks root length 1:1).
- `FullCharSnapshotBuilder.BuildContainers` regenerates the root from DB items, so its length changes;
  `AdjustSnapshotPrefixRootLength` must delta-adjust that varint by `(newRoot − oldRoot)` or the client
  reads the wrong #bytes and misframes the subclass version (the `Wrong IIR_t version` / `ReadSubClass
  failed` family). This adjust is the prime suspect for the regression.

## Investigate in order
1. **Reproduce + capture the exact error.** Run the server with `FAOLAN_WORLDENTRY_DB_CONTAINERS=1` from
   `Faolan/bin/Debug`, drive the client (`Faolan.ClientLauncher … -c Darktemplarchar`), then
   `grep -n "FullPlayerCharacterIIR) failed" AgeOfConan.log`
   (`/var/games/debian-installation/steamapps/common/Age of Conan/AgeOfConan.log`). The error line prints
   `datasize left=… raw=…` — the gap tells you by how many bytes the framing is off.
2. **Diff rebuilt vs verbatim root for the SAME char.** In `RetailLoginReplay.cs` (~line 92–130) the
   verbatim path keeps `parts.Root`/`parts.Prefix`; the DB path rebuilds via
   `FullCharSnapshotBuilder.BuildContainers` + `AdjustSnapshotPrefixRootLength`. Dump both `rootBytes`,
   `newRoot`, the old prefix, and the adjusted prefix; confirm the varint at prefix offset 4 equals
   `newRoot.Length + 4483`. If it doesn't, the adjust math (or the prefix offset after the world-entry
   rework) is wrong.
3. **Check the world-entry rework didn't move the snapshot's neighbours.** The chat-era ordering trap
   (memory `protocol_containeradditem`): ANY packet between the post-ready burst and the snapshot, or a
   changed prefix/base shape, desyncs the client's stream. Confirm the snapshot is still emitted in the
   same position with the same prefix base as the 2026-06-12 working build (`git show 20f2cc1` for the
   then-working code/order).
4. **Typed round-trip is lossy.** `…FromProtobuf<ProtoFullCharacterIIR>()` → mutate → `ToProtobufBytes()`
   drops the per-container IIR version byte `0x18` in some paths (the `37/39` gap, memory
   `fullchar_generation`). If the rebuilt root lost a subclass version byte, that alone causes
   `ReadSubClass failed`. Compare the rebuilt root's container framing byte-for-byte against the verbatim
   one for an unchanged container.

## Acceptance gate
A login with `FAOLAN_WORLDENTRY_DB_CONTAINERS=1` is **IIR-error-free** in `AgeOfConan.log` (no
`ReadSubClass … FullPlayerCharacterIIR failed`), chat still connects (7012 socket opens,
"Authenticating with CommunityServer"), names render, AND the DB bag + equipment doll show the player's
real items. Only then flip the default back ON in `RetailLoginReplay.cs`. Add/extend a byte-exact golden
test alongside `Faolan.Tests/SnapshotPrefixLengthTests.cs` / `FullCharSnapshotBuilderTests.cs` /
`InventorySnapshotTests.cs`.

## Files / context
- `Faolan/GameServer/Login/RetailLoginReplay.cs` (~92–130) — the verbatim-vs-DB-container branch + the
  `FAOLAN_WORLDENTRY_DB_CONTAINERS` flag (currently `== "1"`, opt-in) + `AdjustSnapshotPrefixRootLength`.
- `Faolan/GameServer/Packets/FullCharSnapshot/FullCharSnapshotBuilder.cs` — `BuildContainers`.
- `Faolan/GameServer/Packets/FullCharSnapshot/BeachSnapshotPrefix.cs` — prefix / root-length varint.
- Tests: `Faolan.Tests/{SnapshotPrefixLengthTests,FullCharSnapshotBuilderTests,InventorySnapshotTests,
  BeachSnapshotConstructionTests,SnapshotItemBuilderTests}.cs`.
- Memories: `protocol_containeradditem` (the full saga incl. the root-length-varint crack and the
  2026-06-13 regression note), `fullchar_generation`, `snapshot_tails_construction_scope`,
  `inventory_db_items_work`.

## How to run / test
- Server: `cd Faolan/bin/Debug && FAOLAN_WORLDENTRY_DB_CONTAINERS=1 dotnet Faolan.dll`.
- Client: `dotnet run --project Faolan.ClientLauncher -- --console --universeaddr 127.0.0.1:7010 -u faolan -p any -c Darktemplarchar`.
- After a run: `git checkout -- faolan.db`. **Ignore RDB/resource/AnimSys lines in the client log** — the
  signal is the `FullPlayerCharacterIIR` ReadSubClass line and the `BassiNet.GameClient - …CommunityServer`
  lines.
- Build/tests: `dotnet build ProjectFaolan.slnx`; `dotnet test Faolan.Tests --filter "FullyQualifiedName~Snapshot"`.

## Paste-ready prompt
> Re-enable the DB-container world-entry inventory (read `docs/db-container-handoff.md`). The player's bag +
> equipment-doll delivery via the world-entry `0x737A6DF9` snapshot (`FAOLAN_WORLDENTRY_DB_CONTAINERS`,
> `FullCharSnapshotBuilder.BuildContainers` + `AdjustSnapshotPrefixRootLength` in `RetailLoginReplay.cs`)
> is currently OPT-IN because the rebuilt root makes the client reject the player's FullPlayerCharacterIIR
> ("ReadSubClass … failed, data invalid"), which breaks names + chat. It worked on 2026-06-12 (commit
> `20f2cc1`), so a world-entry change since then desynced the rebuilt root from the prefix root-length
> varint (offset 4, `varint = rootLen + 4483`). Reproduce with the flag =1, grep `AgeOfConan.log` for
> `FullPlayerCharacterIIR) failed` (ignore RDB/resource noise), diff the rebuilt vs verbatim root for the
> same char, fix the prefix/length-adjust (or the lossy typed round-trip dropping the `0x18` subclass
> version byte), and only flip the default back ON once a login is IIR-error-free, chat still connects, and
> the DB bag + doll render. Add a byte-exact golden test.
