# Character-create editor bursts (issue #189)

RE notes for the **create-editor download stream** — the burst the GameServer streams to the client
while it is in the character-creation editor (between the client's `0x205F` "ready" pokes). The goal of #189
is to construct it from scratch like the login path instead of replaying `RetailCreateEditor*Burst.cs`
verbatim. **Status: `InitialBurst` is fully constructed, and all structured `*IIR` types in
`WorldPrepBurst`/`CreationScriptBurst` are decoded + built + wired** (see §4); the remaining verbatim bursts
(conversation, the 1.1 MB intro-cutscene world-entry) and a few handle-only records are tracked in §4 + #223.
This doc is the decode that makes the construction possible.

Dissector: `Captures/decode_create_editor.py` (validated across all seven `*create*.tshark.json` captures).

## 1. `0x2000` is multiplexed — split by endpoint-class pair first

Every editor download is opcode `0x2000`, but `0x2000` is **not** one packet shape. It multiplexes by the
`(SenderClass, ReceiverClass)` endpoint pair (the usual GameServer RPC-class multiplexing). On a create
editor stream four distinct sub-protocols ride `0x2000`:

| Sender → Receiver class | meaning | framing |
|---|---|---|
| `b171ce13 → 6c67c147` (GameAgent → GameInterface) | **IIR carrier** | `[u32 len][u32 iirType][body]`, `len = 4 + len(body)` |
| `5b9756fc → 28377648` | entity register (the `0x201c`-style handshake) | `[u32 0x0000C350][u32 handle][u32 value]` |
| `604ddba0 → 0c384054` | server-id text token | `[u16 len][ascii hex token]` |
| `6dd55614 → bbd560c8` | ack | `[u32 0x00000001]` |

So the first filter is the class pair, **not** the opcode. Reading `body[4:8]` as an "iirType" on the wrong
pair yields garbage that looks like entity handles (`0x0009FF9x`) — that was the initial red herring.

## 2. IIR record inventory (create-no-worldenter, 98 carrier records, 15 types)

`StatIIR` ×45 · `SimpleDynelFullUpdateIIR` ×18 · `StatUpdateIIR` ×7 · `SimpleCharFullUpdateIIR` ×6 ·
`SetMorphValueIIR` ×5 · `BehaviorControlCenterIIR` ×4 · `CharDCMoveIIR` ×3 · `GameActionListIIR` ×2 ·
`ScryScriptIIR` ×2 · `PlayfieldFIIR` ×1 · `FullPlayerCharacterIIR` ×1 · `GameTimeIIR` ×1 · `CharInPlayIIR` ×1 ·
`MagicBuffIIR` ×1 · `EnergyIIR` ×1. (Longer interactive captures add movement/combat/quest types — same grammars.)

## 3. Decoded grammars

### Fully decoded (constructive builders + golden tests landed)
`Faolan/GameServer/CreateFlow/CreateEditorRecordBuilder.cs`, locked by `Faolan.Tests/CreateEditorRecordTests.cs`.

- **StatIIR** (`0xF98E10B3`), 17 bytes — a single stat set on an entity:
  `[u32 type][u32 handle][u8 flag][u32 statId][u32 value]`.
  `type` = `0x0000C350` (character class) for the avatar's per-stat stream; `flag` ∈ {0,1} (the editor
  streams runs of `flag=1` for one stat id with stepping values). Validated `all == 17 bytes` in every capture.
- **EnergyIIR** (`0x40749B8A`), 13 bytes: `[u32 type][u32 handle][u8 flag][u32 value]`.

### Decoded grammar, construction deferred to the existing spawn builders
The two entity full-updates are the **same grammar as the login/world-entry spawns** — there is nothing
create-specific to decode, only builder wiring to do:

- **SimpleDynelFullUpdateIIR** (`0x66AEDD50`) and **SimpleCharFullUpdateIIR** (`0xFA015807`) both start:
  `[u32 type][u32 handle][u32 0]` then an embedded **ProtoGameCodeDynelIIR** transform
  `[varint len]{ 0a 0f <ProtoVec3 pos> · 12 14 <ProtoQuat rot> · 18 <varint playfield_instance> }`.
  The `ProtoVec3`/`ProtoQuat` are the exact fixed32 forms `SpawnTransformGoldenTests` already locks
  (tags `0D/15/1D[/25]`, little-endian floats).
  - `SimpleDynel` then carries an embedded **ProtoSimpleDynelIIR** (`name="SimpleDynel [---- ]"`,
    `display_name="<localized id=… category="55000" …>"`, `template_item_id`, flags) + a small fixed tail.
  - `SimpleChar` then carries the big **ProtoCoreCharacterIIR**/`ProtoFullCharacterIIR`-family message
    (the chosen looks: `"Stygian Female"` race/sex string, body/looks/stat tables) — the same family the
    FullChar snapshot and `NpcSpawnBuilder` already construct.
- **Key finding for #189:** the editor avatar is just a `SimpleCharFullUpdate` spawn of the player with the
  chosen looks — `NpcSpawnBuilder` + the appearance work ([[character_appearance_morphs_unused]]) already
  cover its construction. The remaining work is **wiring**, not new decoders.
- **Fixed editor pose:** the avatar's transform is **byte-identical in all seven captures** —
  `pos=(262.931, 6.014, 280.970)`, `rot=(0, 0.98769, 0, 0.15643)` (≈18° about Y). The editor always spawns
  the avatar on the same pedestal; only `playfield_instance` (session) and the embedded looks differ.

### Fully decoded + constructive builders (added 2026-06-15)
`CreateEditorRecordBuilder.BuildPlayfieldFiir` / `BuildGameTimeIir`, locked by `CreateEditorRecordTests`.
- **PlayfieldFIIR** (`0x5D85BFC7`, "MapChange"), 103 bytes:
  `[9-byte prefix][Vec3 reference pos (BE floats)][0x62][0x0000C79C][u32 mapId][0][u32 charInstance][0]
   [u32 count][0x00009C50][u32 playfieldToken][8 zeros][0x18][0x00000001][u32 playfieldToken][0x0000CB20]
   [f32 nearClip=15.0][f32 farClip][0][0xFFFFFFFFFFFFFFFF][0x00000007][u32 flags]`.
  The position is the map's reference point; the playfield token (live, per-session) is carried twice. `count`,
  `farClip` and `flags` are passed through (not yet semantically pinned). Byte-exact for maps 4000 + 4010.
- **GameTimeIIR** (`0x4F57DC08`, "SetDayTime"), 29 bytes:
  `[u32 0][u32 0][u64 dayTime][u8 flag][u64 timestamp][f32 rate=1.0]`. Byte-exact.
- **SetMorphValueIIR** (`0xBADF5A4B`), 36–37 bytes — `[type][handle]… "hairmesh_207920" … <float weight>`; the
  morph wiring itself is already covered by [[character_appearance_morphs_unused]].
- **ScryScriptIIR** (`0xA36D3B74`) — the cutscene/editor callback scripts; see `docs/create-cutscene-camera-paths.md`.

## 4. What's left (the #189 backlog)
1. ~~Construct `SimpleCharFullUpdate` (editor scene characters)~~ **DONE** — `CreateEditorCharBuilder.Reconstruct`
   rebuilds every scene-pedestal `SimpleChar` (the "Stygian Female" avatar + the four example models: Saddur /
   Male+Female Template / Stygian Slaver) through `SpawnPacketBuilder`: the **transform** from the typed
   `ProtoVec3`/`ProtoQuat` pose, the **identity/appearance** by decoding + re-encoding the `ProtoCoreCharacterIIR`
   (a byte-identical round-trip — no protobuf drift), with the **playfield instance** (the `18 <varint>` token)
   threaded in. The still-un-lifted pre-IIR scaffold block is carried verbatim from the record (DB-sourcing these
   from the `NpcSpawns` rows is blocked on that lift, #185/#54 — the captured rows are from a different session,
   so their pre-IIR blocks don't match). Self-reconstruction is byte-exact (`CreateEditorSceneCharTests`); the
   full burst still equals `InitialBurst.bin` (`BurstGoldenTests`). The live `Character…` preview stays on
   `IirId` (it carries the live client instance as its handle; also already covered by `PlayerSpawnScaffoldCodec`
   / `SpawnEditorPreview.bin`) — the seam for the eventual live chosen-looks preview.
2. ~~Construct the editor-scene `SimpleDynel` props~~ **DONE** — the 18 props are `WorldObjects` rows on map
   4000 in `faolan.db` (same localized ids + keys). `SendRetailCreateEditorInitialBurst` now takes the
   `IDatabaseRepository` and SOURCES each scene dynel from its row (`BuildSceneDynel`): matched by identity
   (`display_name`), re-addressed via `WorldObjectBuilder.TryBuild(row, handle, playfieldInstance, ...)` to the
   record's handle + the create session's playfield token (the new playfield-instance overload —
   `TryRewritePlayfieldInstance` — rewrites the rows' stored token, which is from a different session), and
   emitted ONLY when byte-identical to the verbatim record (the `RetailLoginReplay` zero-render-risk gate); no DB
   / no row / any drift falls back to verbatim. `CreateEditorSceneDynelTests` proves **all 18 reconstruct
   byte-for-byte** and that the **full DB-driven burst equals `InitialBurst.bin`**. (Until Gitea #222 the 18th —
   localized `3072626`, handle `0x0002A7FE` — had no DB row: it shares an exact position with `347659` and the
   importer's position-keyed `WorldObject` dedup dropped it. Fixed by keying world objects by (position,
   `display_name` identity) in the dedup + replay + coverage; `3072626` now has its own row.)
3. ~~Decode + build `PlayfieldFIIR`/`GameTimeIIR`~~ **DONE & WIRED** — both are now constructed in the burst
   via `CreateEditorRecordBuilder.BuildPlayfieldFiir`/`BuildGameTimeIir`. MapChange is built from the editor
   map id + the session playfield token (carried twice); SetDayTime from the captured day-time/timestamp/rate.
   Wiring `InitialBurst.bin` surfaced an un-pinned u32 at PlayfieldFIIR `[58:62]` (0 in the test capture,
   `0x01000000` here) — added as the `field58` pass-through param. Locked by `BurstGoldenTests`.
4. **Remaining** — every record in `RetailCreateEditorInitialBurst` is now constructed except the live
   `Character…` preview (`IirId`, intentionally verbatim — see item 1). Across `WorldPrepBurst` +
   `CreationScriptBurst`, **every `*IIR` record type with structured per-record variation is now decoded + built
   + wired** (dissectors `Captures/decode_character_action.py` + `decode_create_iir_records.py`, builders in
   `CreateEditorRecordBuilder`, locked by `CreateEditorRecordTests` + the burst goldens):
   - `CharacterActionIIR` (0x6B6FD368) — action/ability grant (26 records).
   - `ComboBookIIR` (0xA8BBEB7F) — combo-book grant (12 records).
   - `StatUpdateIIR` (0x96B8DC59) — `(statId,value)` batch.
   - `SetMorphValueIIR` (0xBADF5A4B) — hair/texture resource morph.
   - `BehaviorControlCenterIIR` (0x642CD3D6) **short form** — behavior register (6 records).
   - `BehaviorControlCenterIIR` (0x642CD3D6) **116-byte long form** (×3, #223) — the 28-byte short-form head plus
     an 88-byte tail that binds the behavior to an **equipped item**: `[u32 index][u32 someId][u32 itemHandle×2]
     [u32 0][u32 1][char[4] tag][byte[16] transform×2][8 ownerStamp×2][12 zero]`. The item handle, the 4-char
     entity tag (`OPC1`/`M9W9`/`CUP5`) and the 16-byte transform (the item's pose as four big-endian u32) all
     match the paired `DispatchAddItem` equipment piece earlier in the same burst; `ownerStamp`
     (`B290805C 29E627CA`) is the fixed creator/timestamp pair that recurs burst-wide. Decoded by
     `decode_create_iir_records.py`, built by `CreateEditorRecordBuilder.BuildBehaviorControlCenterIirLong`.
   - `RegenIIR` (0x28D0AC99, #223) — `[u32 type][u32 handle][u8 flag][u32 v1][u32 v2][u32 v3]` (21 bytes). The
     grammar was pinned by harvesting **590 records across all `*.tshark.json`** captures (it's ×1 in the create
     burst but recurs throughout login/gameplay) and confirmed in the client: `RegenIIR_c::ReadSubClass`
     (Ghidra `FUN_0079dea0`, vtable slot 8) reads exactly three big-endian u32 into +0x2c/+0x30/+0x34 — the three
     resource/regen pools; the lone `flag` byte is the base IIR's (0 in every capture). Decoded by
     `decode_create_iir_records.py` + `Captures/ghidra/DumpRegenReader.java`, built by
     `CreateEditorRecordBuilder.BuildRegenIir`.
   - (`StatIIR`/`EnergyIIR`/`PlayfieldFIIR`/`GameTimeIIR` already had builders.)

   **Still verbatim** (handle-addressed via `IirId`, i.e. the only session-variable field is already
   parameterized; the rest is fixed scripted/state content that needs deeper RE — #223):
   `ScryScriptIIR` (the cutscene callback scripts — overlap the in-client cutscene work). Plus the non-IIR
   `ApplySpell`, `RetailNpcConversationBurst`, and the 1.1 MB intro-cutscene `RetailCreateWorldEntryBurst`
   (render/pacing-sensitive, needs in-client validation).
