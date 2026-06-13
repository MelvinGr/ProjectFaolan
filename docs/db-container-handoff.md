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
