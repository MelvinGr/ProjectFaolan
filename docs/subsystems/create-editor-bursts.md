# Character-create editor bursts

RE notes for the **create-editor download stream** — the burst the GameServer streams to the client
while it is in the character-creation editor (between the client's `0x205F` "ready" pokes). The server constructs
it from typed data like the login path (`Faolan/GameServer/CreateFlow/CreateEditorBursts.cs`): the initial burst
and the creation-script burst are built record by record through their codecs (see §4).

Retail's create stream also carries the new character's starting-kit grants (0x2052 abilities, `ComboBookIIR`
combos, `SetItemStat`, `CharacterAction`/`Stat`/`Energy` records). The server does not send them during creation:
every grant is emitted class-driven by the creation-time loadout seed (`CharacterLoadout.Seed`: `ClassAbilities` +
`GetStartingBarSpells` + `GrantCharacterCombos`), which the create flow reaches via `SendBurstlessWorldEntry`, and a
created character is fully functional in-client without the creation-time copy. The record grammars in §4 cover
those types too. The beach NPC intro conversation routes through the constructed `ConversationService`.

Dissector: `CreateEditorRecordTests` (default-globs and validates across every `*create*.tshark.json` capture).

## 1. `0x2000` is multiplexed — split by endpoint-class pair first

Every editor download is opcode `0x2000`, but `0x2000` is **not** one packet shape. It multiplexes by the
`(SenderClass, ReceiverClass)` endpoint pair (the usual GameServer RPC-class multiplexing). On a create
editor stream these distinct sub-protocols ride `0x2000`:

| Sender → Receiver class | meaning | framing |
|---|---|---|
| `b171ce13 → 6c67c147` (GameAgent → GameInterface) | **IIR carrier** | `[u32 len][u32 iirType][body]`, `len = 4 + len(body)` |
| `28377648 → 5b9756fc` (c2s) | **`RequestAggroStatus`** — the client asking what colour a scene character's overhead name is | two ID32s: `[u32 0x0000C350][u32 self][u32 0x0000C350][u32 target]` |
| `5b9756fc → 28377648` (s2c) | **`DispatchUpdateAggroStatus`** — the reply | `[u32 0x0000C350][u32 target][u32 cCategory]`, `cCategory` 0 friendly / 1 hostile / 2 neutral |
| `604ddba0 → 0c384054` | server-id text token | `[u16 len][ascii hex token]` |
| `6dd55614 → bbd560c8` | ack | `[u32 0x00000001]` |

So the first filter is the class pair, **not** the opcode. Reading `body[4:8]` as an "iirType" on the wrong
pair yields garbage that looks like entity handles (`0x0009FF9x`) — and misreads the `RequestAggroStatus` poll as a
"create-editor instance echo" whose "subopcode" is the `self` id and whose "requestId" is the polled target.

## 2. IIR record inventory (create-no-worldenter, 98 carrier records, 15 types)

`StatIIR` ×45 · `SimpleDynelFullUpdateIIR` ×18 · `StatUpdateIIR` ×7 · `SimpleCharFullUpdateIIR` ×6 ·
`SetMorphValueIIR` ×5 · `BehaviorControlCenterIIR` ×4 · `CharDCMoveIIR` ×3 · `GameActionListIIR` ×2 ·
`ScryScriptIIR` ×2 · `PlayfieldFIIR` ×1 · `FullPlayerCharacterIIR` ×1 · `GameTimeIIR` ×1 · `CharInPlayIIR` ×1 ·
`MagicBuffIIR` ×1 · `EnergyIIR` ×1. (Longer interactive captures add movement/combat/quest types — same grammars.)

## 3. Decoded grammars

### Fully decoded (constructive builders + golden tests)
The per-IIR codecs under `Faolan.Core.Network/Codecs/`, locked by `Faolan.Tests/CreateEditorRecordTests.cs`.

- **StatIIR** (`0xF98E10B3`), 17 bytes — a single stat set on an entity:
  `[u32 type][u32 handle][u8 flag][u32 statId][u32 value]`.
  `type` = `0x0000C350` (character class) for the avatar's per-stat stream; `flag` ∈ {0,1} (the editor
  streams runs of `flag=1` for one stat id with stepping values). Validated `all == 17 bytes` in every capture.
- **EnergyIIR** (`0x40749B8A`), 13 bytes: `[u32 type][u32 handle][u8 flag][u32 value]`.

### Decoded grammar, construction deferred to the existing spawn builders
The two entity full-updates are the **same grammar as the login/world-entry spawns** — there is nothing
create-specific to decode:

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
- The editor avatar is just a `SimpleCharFullUpdate` spawn of the player with the chosen looks —
  `NpcSpawnBuilder` and the appearance model cover its construction.
- **Fixed editor pose:** the avatar's transform is **byte-identical across the create captures** —
  `pos=(262.931, 6.014, 280.970)`, `rot=(0, 0.98769, 0, 0.15643)` (≈18° about Y). The editor always spawns
  the avatar on the same pedestal; only `playfield_instance` (session) and the embedded looks differ.

### Fully decoded + constructive builders
`PlayfieldFCodec` / `GameTimeCodec`, locked by `CreateEditorRecordTests`.
- **PlayfieldFIIR** (`0x5D85BFC7`, "MapChange"), 103 bytes. Framed the way the client reads it — its
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

  The proxy's trailing bool is its own byte, not the first byte of a leading zero word; framed the second way
  everything after it shifts one byte, `sceneWord` reads as a u32 `0x01000000` whose only meaningful byte is its
  top one, and `blobLastWord` reads as a top-level word when it is the last word **inside** the blob. The bytes
  come out identical either way, so a golden cannot tell the two framings apart — only the client's reader can.
  Read aligned, `sceneWord` is a plain 0 or 1 (54 / 97 across the captures).

  Neither `sceneWord` nor `blobLastWord` is semantically pinned [V0]: `sceneWord` is 0 on maps 4020/4022 and 1 on
  1000/1010/2000/2010/2040/3000/5007/11040, while 4000 and 4010 carry both; `blobLastWord` takes 0/1/2/3 *within*
  a single map. `count`, `farClip` and `flags` are likewise passed through. The two genuinely per-map words —
  the proxy's slot `[2]` and `fieldC` — come from `Map.PlayfieldFieldA` / `Map.PlayfieldFieldC`.
- **GameTimeIIR** (`0x4F57DC08`), 29 bytes:
  `[u32 0][u32 0][u8 isToBePassed][f32 dayTime][u32 word][u32 gameDay][u32 unixSeconds][f32 rate=1.0]`. Byte-exact.
  `dayTime` is a **float** count of game seconds (86400 to a game day, advancing 5 per real second) and the anchor is
  `[unix / 17280][unix epoch seconds]` — see [`../re/gametime-clock.md`](../re/gametime-clock.md), including why a
  `[u64 dayTime][u8 flag]` reading produces the same bytes and still breaks the clock. The editor burst keeps
  its captured `dayTime` (`0x47929B00` = 75062s; the creation backdrop has no day/night to track, and it keeps the
  burst golden byte-exact); the world clock is live.
- **SetMorphValueIIR** (`0xBADF5A4B`), 36–37 bytes — `[type][handle]… "hairmesh_207920" … <float weight>`.
- **ScryScriptIIR** (`0xA36D3B74`) — the cutscene/editor callback scripts; see `docs/subsystems/create-cutscene-camera-paths.md`.

## 4. Burst record construction

Every record in the initial burst (`CreateEditorBursts.SendRetailCreateEditorInitialBurst`) is constructed from typed data (byte-exact vs `InitialBurst.bin`).

- **`SimpleCharFullUpdate` (editor scene characters)** — **typed data**, in
   [`CreateEditorScene`](../../Faolan.Core.Network/Constants/CreateEditorScene.cs), built through the ordinary
   `SimpleCharFullUpdateCodec` like any other spawn. The five pedestals (the "Stygian Female" avatar + Saddur /
   Male+Female Template / Stygian Slaver) and the live `Character…` preview are all emitted from it, with the
   **playfield instance** set on the model — and, for the preview, the session's client instance as its handle.

   That file is **generated from the captured burst** and regenerated by
   `FAOLAN_UPDATE_SCENE=1 dotnet test --project Faolan.Tests -- --filter-class '*CreateEditorSceneGeneratorTests'`, so its values are a parse
   of the capture rather than a transcription. `CreateEditorSceneCharTests` builds each record from it and requires
   the captured bytes back exactly, which is what makes hand-editing it unnecessary and unsafe.

   `CreateEditorCharBuilder` holds only the playfield token. The pre-IIR is `SpawnPreIir`'s 53 named stat words and
   the post-IIR is `SpawnPostIir`'s typed stream, both decoded whole — which is what makes the typed emit possible.
   The full burst equals `InitialBurst.bin` (`BurstGoldenTests`).

   **The pedestals' pre-IIR words are the retail server's uninitialised memory, not data.** Resolving the
   slots they occupy names them `PvPAnnihilationMatchesWonSeasonal`, `PvPCTFMatchesLostSeasonal`,
   `Onslaught2SpecialsKilled`…, and a create-editor pedestal has no seasonal PvP record: `0x7151E7E0` in a stat
   slot is a leaked pointer. The live avatar in the same burst carries none of them — only the category word and
   the 15.0 constant, which is exactly `SpawnPreIirs.Minimal`. So these are not irreducible per-session values
   blocking a from-data build.
- **Editor-scene `SimpleDynel` props** — the 18 props are `WorldObjects` rows on map
   4000 in `faolan.db` (same localized ids + keys). `SendRetailCreateEditorInitialBurst` takes the
   `IDatabaseRepository` and sources each scene dynel from its row: `LoadEditorSceneDynels` loads the map-4000
   `WorldObjects` keyed by **display id** — the prop's LDB string reference, which is placement truth and unique
   across the map — then the burst loops `EditorSceneDynels`, which pairs each captured WIRE handle with the display
   id that finds its row, and builds each byte-exact via
   `WorldObjectCodec.Build(row, handle, playfieldInstance)` (the playfield-instance overload),
   re-addressing every prop to the captured handle + the editor playfield instance
   (`CreateEditorCharBuilder.EditorPlayfieldInstance`); a no-DB run simply omits them (no verbatim fallback).
   The lookup must not key on a captured wire handle — a session-volatile value as a persistence key: importing a
   capture whose session re-observed the editor scene renumbers all 18 and the props silently vanish from the burst.
   A world object's wire handle is minted by its zone (`LiveWorldObject`).
   `CreateEditorSceneDynelTests` proves **all 18 reconstruct
   byte-for-byte** and that the **full DB-driven burst equals `InitialBurst.bin`**. World objects are keyed by
   (position, `display_name` identity): the 18th — localized `3072626`, handle `0x0002A7FE` — shares an exact
   position with `347659`, so a position-only key drops it.
- **`PlayfieldFIIR`/`GameTimeIIR`** — both are constructed in the burst
   via `PlayfieldFCodec`/`GameTimeCodec`. MapChange is built from the editor
   map id + the session playfield token (carried twice); GameTime from the captured day-time/timestamp/rate.
   The per-capture word is `sceneWord`, a plain `0`/`1` at `[55:59]` (see the table above), passed through.
   Locked by `BurstGoldenTests`.
- **The avatar's FullChar snapshot** — the create editor sends the new character its own `0x737A6DF9`
   `FullPlayerCharacterIIR` on a `DispatchPassBlob`. It is **constructed whole from typed data**
   (`CreateEditorAvatar.Snapshot`), not replayed. Byte-exact against the captured record for the first, last and a
   mid-range character id; pinned by SHA-256.
   [V3: EditorAvatarSnapshotTests.TheConstructedRecord_IsTheCapturedOne]

   | region | source |
   |---|---|
   | root | `subscription_flags = 769` + four empty containers (`0x65` 1028, `0x68` 24, `0x70` 48/12 disabled, `0x71` 48) owned by the live character |
   | quest regions | all three empty — an unnamed avatar has no journal |
   | access window | `AccessWindowCatalog`, the same game-static 86 entries every character gets |
   | stat block | `CreateEditorAvatarSheet` — 17 displayed + 148 sheet pairs, the only captured VALUES left |
   | spellbook / tails | **absent**, and both trailing tails empty |

   Two things had to be right for the bytes to match. **`has_new_claim_items` and `has_spellbook` must be set
   explicitly** — both are optional protobuf scalars, so protobuf-net omits a default-valued one and the root comes
   out four bytes short of retail's (the presence trap). And `has_spellbook = false` is not incidental: it is what
   tells the client the sub-class carries **no** spellbook list, which is exactly how a create-editor snapshot
   differs from a login one on the wire (a login snapshot always carries the list, even empty). The flag and the
   absent list have to agree, which is why this does not just call `SnapshotTails.FromScratch` — that always emits
   the list, correctly, for world entry.

   `CreateEditorAvatarSheet` is a baseline in its own right and is **not** `SubClassStatBlock.FreshSheet()`: same
   stat ids in the same order, ~15 different values (the health/stamina/mana pools, `0x113`, `0x3A`/`0x3B`, `0x6A`,
   the `0x398..0x39D` resist run at −300, `0x25F`). The beach baseline is a level-1 character standing in the world;
   this one is the editor pedestal avatar. The playfield token in it is the named
   `CreateEditorCharBuilder.EditorPlayfieldInstance`, not a literal lifted out of a capture.
- **Record coverage** — no record is capture-shaped: the scene characters are typed data (above), the live
   `Character…` preview included. **Every `*IIR` record type with structured per-record variation in the create
   stream is decoded and has a builder** in the per-IIR codecs, locked by `CreateEditorRecordTests` + the burst
   goldens:
   - `CharacterActionIIR` (0x6B6FD368) — action/ability grant (26 records).
   - `ComboBookIIR` (0xA8BBEB7F) — combo-book grant (12 records).
   - `StatUpdateIIR` (0x96B8DC59) — `(statId,value)` batch.
   - `SetMorphValueIIR` (0xBADF5A4B) — hair/texture resource morph.
   - `BehaviorControlCenterIIR` (0x642CD3D6) **short form** — behavior register (6 records).
   - `BehaviorControlCenterIIR` (0x642CD3D6) **116-byte long form** (×3) — the 28-byte short-form head plus
     an 88-byte tail that binds the behavior to an **equipped item**: `[u32 index][u32 someId][u32 itemHandle×2]
     [u32 0][u32 1][char[4] tag][byte[16] transform×2][8 nullIdentity×2][12 zero]`. The item handle, the 4-char
     entity tag (`OPC1`/`M9W9`/`CUP5`) and the 16-byte transform (the item's pose as four big-endian u32) all
     match the paired `DispatchAddItem` equipment piece earlier in the same burst. The two 8-byte slots are the
     `NullIdentity` sentinel (`B290805C 29E627CA`) — two *unset identity* slots, matching the doubling of every
     field before them, not a creator/timestamp pair. Built by `BehaviorAnimations.ItemRegistration`.
   - `RegenIIR` (0x28D0AC99) — `[u32 type][u32 handle][u8 flag][u32 v1][u32 v2][u32 v3]` (21 bytes). The
     grammar was pinned by harvesting **590 records across all `*.tshark.json`** captures (it's ×1 in the create
     burst but recurs throughout login/gameplay) and confirmed in the client: `RegenIIR_c::ReadSubClass`
     (Ghidra `FUN_0079dea0`, vtable slot 8) reads exactly three big-endian u32 into +0x2c/+0x30/+0x34 — the three
     resource/regen pools; the lone `flag` byte is the base IIR's (0 in every capture). Reader recovered with
     `tools/ghidra/DumpRegenReader.java`, built by `RegenCodec`.
   - `StatIIR` (0xF98E10B3) / `EnergyIIR` (0x40749B8A) / `StatUpdateIIR` (0x96B8DC59) — built via their codecs
     (the per-class StatIIR runs as loops), as are `PlayfieldFIIR`/`GameTimeIIR`, the `id`-frame
     `GameActionListIIR` (0xF508F4C1, `GameActionListCodec`) and `CharInPlayIIR`.

   **`ScryScriptIIR` is constructed:** the two CreationScript records (the "Slave Galley" PreloadAssets
   step + "PlayOrSkip") are built by `ScryScriptCodec.Build`, and the post-morph `StartScriptGroupOnClient`
   callback by its script-group `Build` overload (the NicknameOk/CharCreationFinished callbacks are built by
   `SendA36DCallback`). All byte-exact (`ScryScriptCreateBurstTests`).

   **The post-morph class-base `StatUpdateIIR` is per class.** The trio the editor sends once the
   customization batch drains is `StatIIR` + `StartScriptGroupOnClient` + a `StatUpdateIIR` carrying the three
   base pools — stat `0x0001` health, `0x01F9` stamina, `0x01FB` mana. A barbarian create dump carries
   `11000 / 17200 / 0`, which is **RDB 1000511's Barbarian row**, not an editor-wide constant: retail sends the
   character's own base there — the guardian create carries `12000 / 17250 / 0`, the Guardian row to the unit
   [V2: Blablabla_soldier-guardian_create_random-actions-combat-levelup-questcomplete.tshark.json frame 2613].
   It is built from `CharacterMaxStats.ClassBaseHundredths`, the same table the snapshot's own max slots use
   [V3: CreateEditorClassBaseTests.GuardianLevel1_ReproducesTheCapturedEditorPush_ByteExact].

   **`0x96B8DC59` carries a handle at body offset 12 and it must be filtered on.** The create captures are
   full of `HpCur/StamCur/ManaCur = 6300` triples that read as the player's vitals being overwritten; they go to a
   different handle each time and are **scene NPCs**. Reading them as the avatar's makes a per-class constant look
   like an editor-wide one.

   **Still verbatim:** the 54-byte `MagicBuffIIR` apply payload (spell `0xB90`) in the creation-script burst
   (`CreateEditorBursts.cs`) — the record is built by `MagicBuffCodec`, its payload bytes are the capture's.
