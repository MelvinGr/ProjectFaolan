# NPC three-tier architecture (template / spawn-point / live)

Status: **in progress** (#244 — lifting the stat core off the captured `Data` blob so NPC spawns are
constructed from typed storage, not spliced from a capture). See also
[world-packets construct-not-replay], `docs/mid-session-spawn-blocker.md`, and the simulation-layer roadmap.

## The three tiers

An NPC in the world is three separate concerns; the code keeps them in three separate places:

| tier | store | lifetime | holds |
|------|-------|----------|-------|
| **`Npc`** (template) | DB (`Npcs`) | permanent | The creature TYPE: the canonical stat core shared by every spawn of that type — `ProtoCoreCharacterIIR` fields that are constant across a type (race/sex, monster mesh/data, senses, profession, faction/community, flags, the animation *blend* flags, …). One row per type. |
| **`NpcSpawn`** (spawn point) | DB (`NpcSpawns`) | permanent, **static** | WHERE and WHAT should spawn in the world: map + position + rotation + a ref to the `Npc` template (`NpcId`), plus the per-spawn STATIC config that varies between placements of the same type (level and level-scaled pools, monster scale, voice, faction override). **No gameplay/runtime state.** This table must not change as the world is played. |
| **`WorldEntity`** (live) | **in-memory** (`Faolan/GameServer/World/WorldEntity.cs`) | ephemeral (per session) | The active instance: `Handle`, current `Position`, current HP (`EntityVitals`), behaviour state, `InstanceId`, `OwnerHandle`. Built at spawn, mutated by gameplay, gone on restart. Pets, summons (`OwnerHandle`) and loot bags live ONLY here — they have no `NpcSpawn` row. |

The live tier is deliberately **not persisted**. Runtime state is ephemeral: on restart every active spawn
is regenerated from the static `NpcSpawn` placement + the `Npc` template, and the world boots clean. A
persisted "live NPC" table would be write-hot and buy nothing (you don't want NPC HP/positions to survive a
reboot).

## How a spawn body is built

The wire `FA015807` (SimpleCharFullUpdate) body is CONSTRUCTED at spawn time by composing the three tiers —
never spliced from a captured `Data` blob:

```
spawn body = Npc(template stat core)
           + NpcSpawn(static per-spawn config: level/scale/voice/faction)
           + WorldEntity(canonical runtime init: full HP, idle animation, idle BccState, live handle/pos/token)
```

The **runtime/transient** fields (animation phase, `BccState`, current-HP) come from the live tier as
**canonical** values (full HP, idle) — they are NOT replayed from a capture. A fresh spawn is idle and at
full health, not frozen at whatever moment it happened to be captured in (this also fixes the latent bug
where an NPC captured mid-death would spawn dead). For an idle reference capture the canonical values ARE
the captured values, so the byte output is unchanged; only mid-action captures differ.

`NpcSpawnBuilder.TryBuild` (a placed spawn) and `TryBuildFromType` (a pet/summon with no `NpcSpawn` row)
both build this way; the transform's playfield token is threaded in at construction (no post-hoc
`SpawnPlayfieldRewriter` patch).

## Type-level vs instance-level fields (the split)

Determined empirically by decoding every spawn's stat core and grouping by `NpcId` (which fields are
constant across a type's spawns vs which vary). This is the source of truth for which column goes on which
table:

**Type-level → `Npc`:** `Race`, `Sex`, `HeadMesh`, `MonsterData`, `Flags`, `ChatId`, `CarsGroup`,
`OrientationMode`, `Radius`, `Side`, `SoundScryScript`, `Bcc`, `BloodType`, `LooksPackage`, `Mount*`,
`MovementMultiplier` (CharacterStats) · `NpcFlags`, `NpcType`, `Normalset`, `Profession*`, `SmellingSkill`,
`SuperFamily`, `Community`, `TemplateId` (NpcStats) · the `Anim*Blend` / `AnimFlags` / `AnimForced*` flags (Bcc).

**Instance-level → `NpcSpawn`:** `Level`/`ApprenticeLevel` and the level-scaled `Health`/`Life`/`Mana`/
`MaxMana`/`Stamina`/`MaxStamina`, `MonsterScale`, `VoiceSet`, `GuildId`, `HearingSkill`/`SightSkill`
(CharacterStats/NpcStats).

**Runtime → `WorldEntity`, canonical at spawn (not stored):** the animation phase (`AnimActionId`/`Timer`,
`AnimIdleId`/`Timer`), `BccState`, current HP.

Constant-everywhere fields (`Side`, `MovementMultiplier`, `Mount*`, the `*Blend` flags, `SpawnAnimation`
which is zeroed on every fresh spawn) are hardcoded in the builder rather than stored.

## Feasibility (verified)

Decoding all 1043 captured stat cores confirmed the lift can be byte-exact:
- **No protobuf extension/unknown fields** on the root IIR or any of `CharacterStats`/`NpcStats`/`CharacterBcc`
  (max ext bytes = 0 everywhere), so a message reconstructed from typed columns re-serialises identically.
- Root `ProtoCoreCharacterIIR` scalars beyond the sub-messages: `Name` (already lifted), `CanFall` +
  `MovementMode` (set on every row), and `PetOwnerInstance`/`PetType`/`PetPoints` (pets only → runtime tier).
- The runtime fields (`Anim*Id`/`Timer` phase, `BccState`, current HP) are emitted **canonical** at spawn, so
  they are NOT stored; `AnimIdleId` (the creature's idle clip) is type-level and lives on `Npc`.

## Migration status (#244)

Goal: retire `Npc.Data` / `NpcSpawn.Data` and the `SpawnPacketBuilder` splice; construct every NPC spawn
from typed columns.

- [x] `NpcStatCoreCodec` (explicit if-mapping, no reflection): Lift(msg) -> NpcStatCore and
      ToStats/ToNpcStats/ToBcc back. Verified byte-exact across all 3097 captured sub-messages.
- [x] `NpcStatCore` 1:1 side table (`NpcStatCores`, keyed by NpcId) + migration - the type-level core.
- [x] Importer lifts the stat core: `UpsertNpcTypes` from a representative spawn, `CapturePetImporter` from
      `Npc.Data` for summon-only pets. `faolan.db` re-imported: 521/537 types (16 have no capture at all).
- [x] Per-placement stat columns on `NpcSpawn` (`Life`/`Mana`/`MaxMana`/`Stamina`/`MaxStamina`/`ApprenticeLevel`/
      `VoiceSet` non-null; `GuildId`/`HearingSkill`/`SightSkill` nullable) + importer populate. These override the
      type core per placement (a spawn point carries its own level; the pools track it). Pools are ~91% `Health*100`
      but deviate per creature, so stored (derivable later if a curve is decoded).
- [x] `NpcSpawnBuilder.ApplyStatCore` composes the CharacterIIR stat core from `row.Npc.StatCore` (type) +
      `row` (placement overrides), with runtime anim/state canonical. Verified byte-exact for the type +
      placement fields across all 980 populated spawns (`NpcSpawnStatCoreTests`). `Data` still supplies the
      frame + name + equipment structure.
- [x] `NpcSpawnBuilder.BuildFromScratch` assembles the WHOLE body with no template: header constants;
      transform `2C 0A <vec3> 12 <quat> 18 <token varint>` (token written in-place, no `SpawnPlayfieldRewriter`);
      pre-IIR = `NpcSpawnPreIir.BuildMinimal`; CoreChar header `[41][X][msgLen]` with X = the lifted
      `NpcStatCore.IirHeader` (RE: type-stable, `ReadIirHeaderWord`); ANIMAL post-IIR = `BuildMinimalPostIir`,
      clean-HUMANOID post-IIR = `NpcSpawnAppearanceCodec.BuildPostIir(morphs)`.
- [x] `NpcSpawnBuilder.BuildMessageFromColumns` builds the whole `ProtoCoreCharacterIIR` from the DB (Name +
      `ApplyStatCore` + equipment rows). Whole-DB gate (`NpcSpawnFromScratchTests`): **824/988 build byte-exact
      from columns**, 9 canonically cleaner (drop a captured runtime target), 155 the humanoid runtime-post-IIR
      tail (keep `Data`).
- [ ] WIRE it into the live path: at spawn build the body via `BuildMessageFromColumns` +
      `BuildFromScratch(zoneToken)` for the clean cases (fall back to the `Data` splice for the 155
      runtime-post-IIR humanoids), so `SpawnBody` carries the live token at construction and `SendWorldSpawn`
      drops the `SpawnPlayfieldRewriter` token patch. Byte-neutral for the 824 (verified) - a low-risk
      in-client smoke test. `Data` + the splice + `SpawnPlayfieldRewriter` then remain only on the #243 tail.
- [ ] #243: decode the humanoid RUNTIME post-IIR so the last ~155 drop `Data` too. RE of the tail (of 847
      equipped humanoids: 600 clean-appearance already build from scratch):
      - **148 = no-morph humanoids**: a 41-byte post-IIR byte-identical to the ANIMAL minimal form except
        `recordCount=2` (vs 1) and `count=0` (no morph entries). Build with a `recordCount`-parameterised
        `BuildMinimalPostIir(2)`.
      - **99 = runtime-bearing**: a large first record (movement/bounds float triples, `recordCount=1`,
        `firstRecordLen` large) with the **morph table embedded after it** (`[count][morphId f32 0 0 strLen str]…`
        + the shared 0x3F1 terminator). A canonical fresh spawn drops the runtime record and keeps the morphs, so
        a lenient morph extractor (skip the runtime record, decode the trailing morph table) → `BuildPostIir(morphs)`.
      - 2 = a morph ENTRY carries runtime in field2/field3 (decode later).
      Both main shapes are recoverable; needs a per-spawn post-IIR-kind flag (appearance / minimal-humanoid /
      runtime) lifted by the importer so `TryBuildLive` picks the builder without the captured `Data`.
- [ ] Migrate the remaining `Npc.Community`/`Level` readers (now `[Obsolete]`) to `StatCore`.
