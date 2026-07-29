# Character-create editor bursts (issue #189)

RE notes for the **create-editor download stream** — the burst the GameServer streams to the client
while it is in the character-creation editor (between the client's `0x205F` "ready" pokes). The goal of #189
is to construct it from scratch like the login path instead of replaying `RetailCreateEditor*Burst.cs`
verbatim. **Status: `InitialBurst` is fully constructed, and all structured `*IIR` types in
`WorldPrepBurst`/`CreationScriptBurst` are decoded + built + wired** (see §4; #223 closed).

> **`WorldPrepBurst` has since been REMOVED entirely** (`0683e84d`). It replayed the guardian capture's
> starting-kit grants (0x2052 abilities, `ComboBookIIR` combos, `SetItemStat`, `CharacterAction`/`Stat`/`Energy`
> records) during creation — all proven redundant in-client: a created necromancer is fully functional without
> it, because every grant is re-emitted class-driven by the creation-time loadout seed (`CharacterLoadout.Seed`:
> `ClassAbilities` + `GetStartingBarSpells` + `GrantCharacterCombos`), which the create flow already reaches via
> `SendBurstlessWorldEntry`. Its only unique effect was the live create-session quickbar placement of the 3 basic
> attacks, which the shared bar-seed covers for a fresh character. The §4/§5 notes below describe the decode work
> as it stood while the burst existed — kept as the record of what those record types are.

The remaining
verbatim burst is the intro-cutscene world-entry
`RetailCreateWorldEntryBurst`, whose construction is now tracked separately under **#234** (its ScryScript +
despawn + regen + BehaviorCC records — 146 in all — are already constructed; the FA015807 spawns + MoveTask/AI
choreography remain, gated on #185/#190). The former verbatim beach NPC intro conversation
(`RetailNpcConversationBurst`/`SendNpcIntroConversation`) has since been retired — the beach click now routes
through the live constructed `ConversationService` (#188, `d208fc82`). This doc is the decode that makes the
construction possible.

Dissector: `CreateEditorRecordTests` (default-globs and validates across every `*create*.tshark.json` capture).

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
`Faolan.Core.Network/Codecs/Spawn/CreateEditorRecordBuilder.cs`, locked by `Faolan.Tests/CreateEditorRecordTests.cs`.

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
- **Fixed editor pose:** the avatar's transform is **byte-identical across the create captures** —
  `pos=(262.931, 6.014, 280.970)`, `rot=(0, 0.98769, 0, 0.15643)` (≈18° about Y). The editor always spawns
  the avatar on the same pedestal; only `playfield_instance` (session) and the embedded looks differ.

### Fully decoded + constructive builders (added 2026-06-15)
`CreateEditorRecordBuilder.BuildPlayfieldFiir` / `BuildGameTimeIir`, locked by `CreateEditorRecordTests`.
- **PlayfieldFIIR** (`0x5D85BFC7`, "MapChange"), 103 bytes. Framed the way the CLIENT reads it (#451) — its
  reader is `PlayfieldFIIR_c`'s `ReadSubClass` = `FUN_008f36e0`, which calls the base `FUN_013d0a20` and then
  reads the four trailing words:

  | body offset | field | client |
  |---|---|---|
  | `@0..8` | `[u32 0xC350][u32 handle][u8 env]` | the IIR base carrier |
  | `@9..20` | `[f32 X][f32 Y][f32 Z]` the map's reference point | `obj+0x24 / +0x28 / +0x2C` |
  | `@21..54` | an embedded **`PlayfieldProxy`** (`FUN_00a6a600`) | slot `[1]` = map, `[2]` = `Map.PlayfieldFieldA`, `[3]` = char instance, `[5]` = count, `[7]` = playfield token |
  | `@55..58` | `[u32 sceneWord]` | `obj+0x54` |
  | `@59..86` | `[u32 len = 24][24-byte blob]`, a `FUN_00cbf5d0` length-prefixed sub-stream | blob = `[u32 1][u32 playfieldToken][u32 0x0000CB20][f32 nearClip=15.0][f32 farClip][u32 blobLastWord]` |
  | `@87..102` | `[u32 -1][u32 -1][u32 fieldC][u32 flags]` | `obj+0x80 / +0x84 / +0x88 / +0x8C` |

  Validated over **all 151 captured PlayfieldFIIR bodies**: every one is 103 bytes, carries the proxy's `'b'`
  version byte at `@21`, and its blob-length word reads exactly 24, so `59 + 4 + 24 + 16 == 103`. Zero failures.
  [V1: tools/ghidra/DecompileWithCallees.java — `FNS=008f36e0` then `FNS=013d0a20 DEPTH=2`]

  Two things this framing corrected. The builder used to write the proxy's trailing bool as the first byte of a
  leading zero word, which pushed everything after it one byte out: `sceneWord` was therefore modelled as a
  `field58 = 0x01000000`, a u32 whose only meaningful byte was its **top** one, and `blobLastWord` (`fieldB`) was
  modelled as a top-level word when it is really the last word **inside** the blob. Read aligned, `sceneWord` is a
  plain 0 or 1 (54 / 97 across the captures). The bytes were always right — which is exactly why the goldens
  never caught it.

  Neither `sceneWord` nor `blobLastWord` is semantically pinned [V0]: `sceneWord` is 0 on maps 4020/4022 and 1 on
  1000/1010/2000/2010/2040/3000/5007/11040, while 4000 and 4010 carry both; `blobLastWord` takes 0/1/2/3 *within*
  a single map. `count`, `farClip` and `flags` are likewise passed through. The two genuinely per-map words —
  the proxy's slot `[2]` and `fieldC` — come from `Map.PlayfieldFieldA` / `Map.PlayfieldFieldC`.
- **GameTimeIIR** (`0x4F57DC08`), 29 bytes:
  `[u32 0][u32 0][u8 isToBePassed][f32 dayTime][u32 word][u32 gameDay][u32 unixSeconds][f32 rate=1.0]`. Byte-exact.
  `dayTime` is a **float** count of game seconds (86400 to a game day, advancing 5 per real second) and the anchor is
  `[unix / 17280][unix epoch seconds]` — see [`../re/gametime-clock.md`](../re/gametime-clock.md), including why the
  earlier `[u64 dayTime][u8 flag]` reading produced the same bytes and still broke the clock. The editor burst keeps
  its captured `dayTime` (`0x47929B00` = 75062s; the creation backdrop has no day/night to track, and it keeps the
  burst golden byte-exact); the world clock is live.
- **SetMorphValueIIR** (`0xBADF5A4B`), 36–37 bytes — `[type][handle]… "hairmesh_207920" … <float weight>`; the
  morph wiring itself is already covered by [[character_appearance_morphs_unused]].
- **ScryScriptIIR** (`0xA36D3B74`) — the cutscene/editor callback scripts; see `docs/subsystems/create-cutscene-camera-paths.md`.

## 4. Burst record construction

Every record in `RetailCreateEditorInitialBurst` is constructed from typed data (byte-exact vs `InitialBurst.bin`).

- **`SimpleCharFullUpdate` (editor scene characters)** — `CreateEditorCharBuilder.Reconstruct`
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
- **Editor-scene `SimpleDynel` props** — the 18 props are `WorldObjects` rows on map
   4000 in `faolan.db` (same localized ids + keys). `SendRetailCreateEditorInitialBurst` now takes the
   `IDatabaseRepository` and SOURCES each scene dynel from its row: `LoadEditorSceneDynels` loads the map-4000
   `WorldObjects` keyed by **display id** — the prop's LDB string reference, which is placement truth and unique
   across the map — then the burst loops `EditorSceneDynels`, which pairs each captured WIRE handle with the display
   id that finds its row, and builds each byte-exact via
   `WorldObjectBuilder.TryBuild(row, handle, playfieldInstance, out body)` (the playfield-instance overload),
   re-addressing every prop to the captured handle + the editor playfield instance
   (`CreateEditorCharBuilder.EditorPlayfieldInstance`); a no-DB run simply omits them (no verbatim fallback).
   The lookup used to key on a `WorldObjects.CapturedHandle` column — a session-volatile value as a persistence key.
   Importing a capture whose session re-observed the editor scene renumbered all 18 and the props silently vanished
   from the burst (36 frames → 18), which is why the column is gone entirely: a world object's wire handle is now
   minted by its zone (`LiveWorldObject`).
   `CreateEditorSceneDynelTests` proves **all 18 reconstruct
   byte-for-byte** and that the **full DB-driven burst equals `InitialBurst.bin`**. (Until Gitea #222 the 18th —
   localized `3072626`, handle `0x0002A7FE` — had no DB row: it shares an exact position with `347659` and the
   importer's position-keyed `WorldObject` dedup dropped it. Fixed by keying world objects by (position,
   `display_name` identity) in the dedup + replay + coverage; `3072626` now has its own row.)
- **`PlayfieldFIIR`/`GameTimeIIR`** — both are constructed in the burst
   via `CreateEditorRecordBuilder.BuildPlayfieldFiir`/`BuildGameTimeIir`. MapChange is built from the editor
   map id + the session playfield token (carried twice); GameTime from the captured day-time/timestamp/rate.
   Wiring `InitialBurst.bin` surfaced an un-pinned u32 near PlayfieldFIIR `[58:62]` (0 in the test capture,
   `0x01000000` here) — added as a pass-through param. It is now framed as the client reads it, a plain `0`/`1`
   word at `[55:59]` (`sceneWord`, see the table above). Locked by `BurstGoldenTests`.
- **Record coverage** — the only record left verbatim is the live `Character…` preview (`IirId`, intentionally
   verbatim — see above). Across `WorldPrepBurst` +
   `CreationScriptBurst`, **every `*IIR` record type with structured per-record variation is now decoded + built
   + wired** (the two dissectors were retired with the burst - they read `Faolan.Tests/Golden/WorldPrepBurst.bin`,
   deleted in `0683e84d`; builders in
   `CreateEditorRecordBuilder`, locked by `CreateEditorRecordTests` + the burst goldens):
   - `CharacterActionIIR` (0x6B6FD368) — action/ability grant (26 records).
   - `ComboBookIIR` (0xA8BBEB7F) — combo-book grant (12 records).
   - `StatUpdateIIR` (0x96B8DC59) — `(statId,value)` batch.
   - `SetMorphValueIIR` (0xBADF5A4B) — hair/texture resource morph.
   - `BehaviorControlCenterIIR` (0x642CD3D6) **short form** — behavior register (6 records).
   - `BehaviorControlCenterIIR` (0x642CD3D6) **116-byte long form** (×3, #223) — the 28-byte short-form head plus
     an 88-byte tail that binds the behavior to an **equipped item**: `[u32 index][u32 someId][u32 itemHandle×2]
     [u32 0][u32 1][char[4] tag][byte[16] transform×2][8 nullIdentity×2][12 zero]`. The item handle, the 4-char
     entity tag (`OPC1`/`M9W9`/`CUP5`) and the 16-byte transform (the item's pose as four big-endian u32) all
     match the paired `DispatchAddItem` equipment piece earlier in the same burst. The two 8-byte slots are the
     `NullIdentity` sentinel (`B290805C 29E627CA`) — two *unset identity* slots, matching the doubling of every
     field before them, rather than the "creator/timestamp pair" this was read as while the value had a different
     name in each of the four codecs that write it. Decoded by
     the retired `decode_create_iir_records.py` (see above), built by `CreateEditorRecordBuilder.BuildBehaviorControlCenterIirLong`.
   - `RegenIIR` (0x28D0AC99, #223) — `[u32 type][u32 handle][u8 flag][u32 v1][u32 v2][u32 v3]` (21 bytes). The
     grammar was pinned by harvesting **590 records across all `*.tshark.json`** captures (it's ×1 in the create
     burst but recurs throughout login/gameplay) and confirmed in the client: `RegenIIR_c::ReadSubClass`
     (Ghidra `FUN_0079dea0`, vtable slot 8) reads exactly three big-endian u32 into +0x2c/+0x30/+0x34 — the three
     resource/regen pools; the lone `flag` byte is the base IIR's (0 in every capture). Decoded by
     the retired `decode_create_iir_records.py` + `tools/ghidra/DumpRegenReader.java`, built by
     `CreateEditorRecordBuilder.BuildRegenIir`.
   - `StatIIR` (0xF98E10B3) / `EnergyIIR` (0x40749B8A) / `StatUpdateIIR` (0x96B8DC59) — had builders, and as of
     c2611b23 the last raw `IirId()` byte-literal records in `CreationScriptBurst` / `InitialBurst` /
     `WorldPrepBurst` are now built via `CreateEditorRecordBuilder` too (the per-class StatIIR runs as loops). The
     unused `C350` var + per-file `IirId` helpers were dropped. (`PlayfieldFIIR`/`GameTimeIIR` already wired.)
     The verbatim carriers of this family that are **avatar-directed** (frame = the live `id`) were converted
     the same way (#304): 18× StatIIR in `Post205FBurst` (that burst has since been removed entirely as a dead,
     never-firing path, `022c0ca1`); in `WorldEntryBurst` 5× StatIIR (+2× StatIIR /
     2× StatUpdateIIR / 1× EnergyIIR / 1× FeedbackIIR / 1× CharInPlayIIR) + the live-cutscene EnergyIIR in
     `CreateWorldCutsceneScripts`; plus the 3 `id`-frame `GameActionListIIR` (0xF508F4C1) via
     `GameActionListCodec` and `CharInPlayIIR` in `PostReadyBurst`. **Correction:** an earlier claim of "no
     StatIIR/… verbatim carrier remains in ANY burst" was WRONG — it was blind to the byte-per-line carriers.
     `RetailCreateWorldEntryBurst` (now a test-support replay under `Faolan.Tests/Support/`) still emits
     **decoded-record carriers with capture-specific NPC/dynel handles** — 7× StatUpdateIIR (frames
     0x00017E2x/ED0, built via `CreateEditorRecordBuilder.BuildStatUpdateIir`) — plus the larger verbatim
     FA015807-spawn / MoveTask blobs; those belong to the **#234 intro-cutscene reconstruction** (they must be
     re-addressed to live entities, not merely reframed), not this header-residue cleanup, so they are left
     as-is. (The lone `WorldEntryBurst` GameActionListIIR is now the constructed `id`-frame record counted
     above, not a capture-handle carrier.)

   **`ScryScriptIIR` now CONSTRUCTED (#223):** the two CreationScript records (the "Slave Galley" PreloadAssets
   step + "PlayOrSkip") are built by `ScryScript.BuildStep`, and the post-morph `StartScriptGroupOnClient`
   callback by `ScryScript.BuildStartScriptGroup` (the NicknameOk/CharCreationFinished callbacks were already
   built by `SendA36DCallback`). All byte-exact (`ScryScriptCreateBurstTests`). With `BehaviorControlCenterIIR`
   long form + `RegenIIR` (already done), **no `*IIR` record type in the create-editor bursts is verbatim**.

   **The post-morph class-base `StatUpdateIIR` is PER CLASS (2026-07-29).** The trio the editor sends once the
   customization batch drains is `StatIIR` + `StartScriptGroupOnClient` + a `StatUpdateIIR` carrying the three
   base pools — stat `0x0001` health, `0x01F9` stamina, `0x01FB` mana. That third record used to be a verbatim
   captured blob, `11000 / 17200 / 0`, and it is **RDB 1000511's Barbarian row**: harvested from a barbarian
   create dump and then replayed at every class alike. Retail sends the character's OWN base there — the guardian
   create carries `12000 / 17250 / 0`, the Guardian row to the unit
   [V2: Blablabla_soldier-guardian_create_random-actions-combat-levelup-questcomplete.tshark.json frame 2613].
   It is now built from `CharacterMaxStats.ClassBaseHundredths`, the same table the snapshot's own max slots use
   [V3: CreateEditorClassBaseTests.GuardianLevel1_ReproducesTheCapturedEditorPush_ByteExact].

   ⚠️ **`0x96B8DC59` carries a HANDLE at body offset 12 and it must be filtered on.** The create captures are
   full of `HpCur/StamCur/ManaCur = 6300` triples that read as the player's vitals being overwritten; they go to a
   different handle each time and are **scene NPCs**. Reading them as the avatar's is what made a per-class
   constant look like an editor-wide one.

   **Still verbatim** (deferred): `ApplySpell` (the `MagicBuffIIR` setup blob still emitted verbatim in the
   CreationScript burst, `CreateFlowService` line 930). (The former verbatim beach NPC intro conversation,
   `RetailNpcConversationBurst`/`SendNpcIntroConversation`, was retired — the beach click now routes through the
   live constructed `ConversationService`, #188/`d208fc82`.) The intro-cutscene `RetailCreateWorldEntryBurst` is now constructed
   record-by-record under **#234** (ScryScript + despawn + regen + BehaviorCC done; FA015807 spawns + MoveTask/AI
   remain, gated on #185/#190) — end-to-end in-client validation still pends the cutscene arming (#1).
