# Character-create editor bursts (issue #189)

RE notes for the **create-editor download stream** — the burst the GameServer streams to the client
while it is in the character-creation editor (between the client's `0x205F` "ready" pokes). Today this is
replayed verbatim from `Faolan/GameServer/CreateFlow/RetailCreateEditor*Burst.cs`; the goal of #189 is to
construct it from scratch like the login path. This doc is the decode that makes that possible.

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
1. Construct `SimpleCharFullUpdate` (editor avatar) from the character's chosen looks via `NpcSpawnBuilder`
   instead of replaying the captured blob — the big remaining win; gated on threading the editor pose +
   playfield instance through.
2. Construct the editor-scene `SimpleDynel` props from `ProtoSimpleDynelIIR` rows.
3. Decode `PlayfieldFIIR`/`GameTimeIIR` bodies fully (small, low risk) and build them.
4. The verbatim `RetailCreateEditorInitialBurst`/`CreationScriptBurst`/etc. stay as the interim until each
   record type above is constructed and proven byte-exact, per the project's decode-then-golden discipline.
