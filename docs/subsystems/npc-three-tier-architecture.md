# NPC three-tier architecture (template / spawn-point / live)

Status: **done** (#244 — the stat core is lifted off the captured `Data` blob so NPC spawns are
constructed from typed storage, not spliced from a capture. The `Data` column has now been **retired** and
every shipped spawn builds from typed columns (988/988) via `NpcSpawnBuilder.TryBuildLive` — the sole
spawn-body path). See also
[world-packets construct-not-replay], `docs/subsystems/mid-session-spawn-blocker.md`, and the simulation-layer roadmap.

## The three tiers

An NPC in the world is three separate concerns; the code keeps them in three separate places:

| tier | store | lifetime | holds |
|------|-------|----------|-------|
| **`NpcTemplate`** (template) | DB (`NpcTemplates`) | permanent | The creature TYPE: the canonical stat core shared by every spawn of that type — `ProtoCoreCharacterIIR` fields that are constant across a type (race/sex, monster mesh/data, senses, profession, faction/community, flags, the animation *blend* flags, …). One row per type, keyed by `TemplateId` (the stable RDB `NpcStats.f4` id — a natural-key catalog like `Item`/`Spell`/`Map`). |
| **`NpcSpawn`** (spawn point) | DB (`NpcSpawns`) | permanent, **static** | WHERE and WHAT should spawn in the world: map + position + rotation + a ref to the `NpcTemplate` (`NpcTemplateId`), plus the per-spawn STATIC config that varies between placements of the same type (level and level-scaled pools, monster scale, voice, faction override). **No gameplay/runtime state.** This table must not change as the world is played. |
| **`WorldEntity`** (live) | **in-memory** (`Faolan/GameServer/World/WorldEntity.cs`) | ephemeral (per session) | The active instance: `Handle`, current `Position`, current HP (`EntityVitals`), behaviour state, `InstanceId`, `OwnerHandle`. Built at spawn, mutated by gameplay, gone on restart. Pets, summons (`OwnerHandle`) and loot bags live ONLY here — they have no `NpcSpawn` row. |

The live tier is deliberately **not persisted**. Runtime state is ephemeral: on restart every active spawn
is regenerated from the static `NpcSpawn` placement + the `NpcTemplate` template, and the world boots clean. A
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

`NpcSpawnBuilder.TryBuildLive` (a placed spawn) and `TryBuildFromType` (a pet/summon with no `NpcSpawn` row)
both build this way. **The playfield token, however, must NOT be baked at build/load time** - the
value written at construction is only a placeholder. The token the client accepts is the LIVE playfield
token the entering player loaded, which does not exist until that player enters the world; a spawn
carrying any other token is silently dropped by the client (it never even polls), so `SendWorldSpawn`
**re-stamps the token per-send** via `SpawnTransformPatch` from the recipient's live zone token. A
2026-07-08 regression baked the zone's default load-time token and dropped that re-stamp, which stalled
every world entry (see the memory `spawn_playfield_token_per_send`).

## Type-level vs instance-level fields (the split)

Determined empirically by decoding every spawn's stat core and grouping by `NpcTemplateId` (which fields are
constant across a type's spawns vs which vary). This is the source of truth for which column goes on which
table:

**Type-level → `NpcTemplate`:** `Race`, `Sex`, `HeadMesh`, `MonsterData`, `Flags`, `ChatId`, `CarsGroup`,
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

## The nameplate RANK: `npc_type` is Minion / MiniBoss / Boss (#797)

`NpcStats.npc_type` (field 14, stored as `NpcStatCore.NpcType`) is the NPC's **rank**, and the client renders it
as the title line on the nameplate:

| `npc_type` | title | rows | who |
|---:|---|---:|---|
| 1 | `Minion` | 55 | plain creatures — zero vendors, zero conversations, mostly hostile trash |
| 2 | `MiniBoss` | 10 | Eerika of the Maidens, Pict Chief, Torgrim Blackfrost, Arnulf Iceblade — and the Tortage destiny NPCs |
| 3 | `Boss` | 59 | King Conan, Saddur, Ape King, Commander Vakarr, Battlemaster Torgvall, Head Poacher |
| 4 | *(none)* | 897 | ordinary interactive NPCs — 534 vendors, 513 with a conversation |

A `Normalset == 4` short-circuits the whole thing to `Pray` (the critter tier: Badger, Chicken, Hen, Moose)
before the rank is consulted.

This is a code path, not an inference from the roster. The nameplate builder reads a **non-stat** member —
`[[dynel+0x330]+0x18]` — and DEC/JZ-chains it into the three LDB category-100 tokens; the SimpleChar spawn apply
writes that member from the spawn's own `npc_stats` submessage, alongside four other fields whose destination
stats the client's own name table names (`SuperFamily`, `Normalset`, `NPCProfessionName`, `NPCProfessionType`)
and three more from the sibling `stats` submessage (`LooksPacket`, `HeadMesh`, `BCC`). All seven identities fix
the same protobuf base offset, which is what puts field 14 at the slot being written.
[V1: tools/ghidra/DumpNpcTypeRank.java]

⚠️ **It is NOT the skull rank** — that is `Normalset` (stat 205), pinned separately. And the health ordering
agrees with the ladder (2 runs 98k–937k against 3's up-to-300M) without licensing loot or XP to be derived from
it: a rank title is all the client draws.

⚠️ Value 2 also holds Delia, Sakumbe and Valerius, so `MiniBoss` appears over a friendly Tortage quest-giver
unless the builder gates the title on hostility somewhere off this path. That is retail's own data saying so, not
a decode doubt; one in-client look settles which (#758).

## One placement, several sightings (`PlacementId`, #713/#718)

A captured spawn is keyed on `MapId` + exact XYZ, so **every sighting of a creature that is not exactly where it was
last time mints another placement**. Two mechanisms identify the redundant rows, and neither is a distance:

- **A patroller** (#713) is identified by its harvested **beat**. Rows sharing a `PatrolRoute.PlacementKey` are one
  patroller seen in different sessions — 1,045 route-carrying spawns are 812 placements.
- **A stationary NPC** (#718) has no beat, and its position is *nearly* stable, so a sighting a few centimetres off
  still mints a row. The test is the **HANDLE**: two real placements are two entities and retail mints two handles,
  while one NPC harvested twice is one entity at two moments — so a single handle sighted at two of a cluster's
  positions *walked* between them. `StationaryPlacementResolver`, run by the `npc-placements` module and printed by
  `--report spawn-duplicates`. 9 clusters, 23 rows.

⚠️ **Proximity proves nothing, and neither does "identical in every other column".** Retail really places same-name
NPCs a metre apart — Black Dragon Guards flanking a Tarantia doorway, a pack of Enraged Boars — and those rows are
identical in every column by construction. Of 191 candidate clusters within 5u, **181 are two real placements**. And
"the same capture saw both positions" inverts the answer, because a creature that moves is re-streamed by interest
within one session: read per-capture, the corpus calls the three Scruffys of #765 two real placements; read per
handle, one handle stood on two of them.

Nothing is deleted either way. The redundant rows are the placement's **variant pool** — what differs between them
is exactly what retail re-rolls per spawn (level, the morph table, all seven equipment slots) — and
`WorldBroadcaster` stands up ONE entity per pool, drawing which row it renders as at random.

### ⚠️ The class both mechanisms are BLIND to: cross-map duplicates (#728)

Both key on `MapId`, so **two records of one NPC filed under two map ids are never compared** — and a map's
SUB-AREAS are separate playfield ids sharing one coordinate space, which is exactly how that happens. Measured by
`--report spawn-duplicates`, the whole class is three map pairs:

| pair | pairs within 5u | of the smaller map | level+health agree | one capture filed BOTH ids |
|---|---:|---|---:|---|
| 1090 Ymir's Pass ↔ 1092 The Amphitheatre of Karutonia | 123 | **53 of 53** | **123/123** | no |
| 2000 Old Tarantia ↔ 2020 Tarantia Common District | 42 | 26 of 258 | 32/42 | **15 spots** |
| 4020 City of Tortage ↔ 4022 City of Tortage Night | 10 | 9 of 56 | 4/10 | **5 spots** |

⭐ **The decisive column is the last one, and it needs no timestamps.** If ONE capture filed the same template at
the same spot under BOTH ids, no second population is involved: the extractor re-keyed mid-stream and one physical
NPC was recorded twice. Tarantia and Tortage both fail that test — and Tortage day/night was the standing example
of a pair that *should* be legitimate, one city under two playfields. One of the captures that does it is even
named `move-from-night-to-day`.

⭐ Ymir's Pass ↔ the Amphitheatre is the other shape: **no capture saw it both ways**, yet the Amphitheatre's
population is duplicated in its ENTIRETY (53 of 53) and every one of the 123 pairs agrees on level AND health.
That is one population recorded under two ids by two different captures — the same bug across sessions rather than
within one.

⚠️ **Which side is right is still open**, so nothing is deleted: the measurement says the class is a bug, not
which attribution to keep. Open work tracked in Gitea #728.

## Ambient idle animations (#766)

The spawn body's `AnimIdleId` is the pose a creature HOLDS. What makes it look alive is separate and does not ride
the spawn at all: retail streams a `BehaviorId.Emote` record every so often naming one of the creature's ambient
animations — a citizen leaning on a wall, scratching, looking round.

Those animations are **type-level**, stored per creature as `NpcAmbientAnimation` (`NpcTemplate.AmbientAnimations`)
and harvested from the corpus by the `npc-ambient-animations` module: 767 rows over 222 creature types, 116 distinct
animations. A type no capture ever caught idling has none and stands still — a borrowed animation may not even exist
on another creature's skeleton.

Each row carries the animation's own LENGTH (the record's `activation_time`, which the client is told) and how often
retail was captured playing it. `WorldState.Tick` picks one WEIGHTED by that count while the NPC is genuinely
standing — not chasing, walking a beat, following or engaged, which is retail's own split (24,432 of the 25,750
attributable captured records land on an actor with no MoveTask in the preceding 10s) — and schedules the next for
after this one finishes plus a short pause. See the ambient section of
[behaviorcontrolcenter-grammar.md](../re/behaviorcontrolcenter-grammar.md) for the measurements and
`BehaviorAnimations.AmbientEmote` for the record.

## NPC-follows-NPC: the ambient formation (#741)

*"giner & scruffy should be chasing each other"* names a behaviour we had nothing for. The wire says it is **not a
chase and not mutual**: `Ginger` gets an ordinary multi-waypoint path and `Scruffy` gets a `MoveTaskIIR` whose task
is a **FormationMoveTask** naming Ginger's handle, and Scruffy's own from-position lands within 1.3–3.1 units of
Ginger's every time. One creature leads; the other is bound to it.
[V2: NecroL80_mage-necromancer_login_gameplay_annotated_1.tshark.json frames 1349 / 1521 / 2500 / 2836]

The record's six words are `[class][leaderHandle][offsetX][offsetY][offsetZ][slot]`
([movetask-grammar.md](../re/movetask-grammar.md)). 170 of them over 21 captures give **8 creature-type pairs**:

| follower | leader | records |
|---|---|---:|
| Enslaved Demon | Nemedian Deathstalker | 78 |
| Black Dragon Guard Patroller | Black Dragon Guard Captain Patroller | 25 |
| Scruffy | Ginger | 25 |
| Nemedian Defender | Nemedian Deathstalker | 12 |
| Slave of Acheron | Black Ring Hellsworn | 10 |
| Summoned Incubus | Black Ring Stormcaller | 10 |
| Slave of Acheron | Black Ring Defender | 9 |
| Summoned Succubus | Black Ring Pyromancer | 1 |

They are stored as `NpcFollowPair`, keyed by the TYPE at **both** ends and harvested by the `npc-follow-pairs`
module (`--report follow-pairs` prints the survey). ⚠️ Both ends, not a `FollowsTemplateId` column on the follower:
`Slave of Acheron` was captured behind *two* different leader types, and a single column would have to pick one and
silently drop the other.

⚠️ **The offset is per-INSTANCE and is deliberately not stored.** The four `Black Dragon Guard Patroller`s behind
one captain each hold a different corner of a 3×3 grid with their own slot index; stamping one of them on the type
would stack every follower of that type in the same spot. The runtime derives its own slot from the follower's
handle instead — the same deterministic fan-out a player's pets already use.

At zone load `WorldBroadcaster.BindFormationFollowers` binds each follower to the **nearest** live leader of a type
it walks with, within `FormationBindRange`. Nearest matters: a map carries several groups of the same two types
(five separate `Nemedian Deathstalker` groups in one capture, four `Ginger`/`Scruffy` pairs across three), so
binding to "any leader of that type" would march a follower across the zone. The range is **ours** — the corpus
states the pairing and never a maximum distance — so it is set at the leash territory a patroller already works in.

`WorldState.Tick` then runs the existing companion-follow step. The one thing that had to change there is that the
leader may now be an NPC: the branch required `leader.IsPlayer` and cleared any other binding on the next tick,
which is precisely why this was inexpressible. A leader that dies or leaves the zone still releases its followers.

⚠️ **Not modelled:** re-binding when a leader respawns (the follower simply keeps its own behaviour until the next
zone load), and chains (nothing in the corpus has a follower that is itself a leader).

## The four per-spawn-event fields, and who corrects them (#605/#688/#689)

`MovementMode`, `npc_stats.SpawnAnimation`, `Stats.BccState` and `Bcc.AnimIdleId` are **rolled per spawn
event**, not properties of the type. A type's core is lifted from ONE representative sighting, so whatever that
NPC happened to be doing at capture time would become the type's definition of itself and be inherited by every
placement. `PerSpawnEventModes` tallies each field over **all** of a type's sightings and keeps the **mode**.

⚠️ Not a blanket reset to a resting value. 14 types are never captured in `BccState` 1 — the Door Warden
(19619) is state 11 in all 15 of its sightings — so a type *can* rest somewhere other than the common value.

⭐ **`SpawnAnimation` is a decaying STATE, not merely per-event** (#607). Over the 4,687 entities sighted more than
once, 351 change their answer, 346 of those are ON for the first sighting and off thereafter, and **not one ever
turns ON** (`--report spawn-animation`).

⭐ **And the CLIENT says what the state is a state of: "you have not played your entrance animation yet"** (#800).
The wire field lands on client stat `0x19C SpawnAnimation` — `FUN_01458d40` copies `npc_stats+0x24` straight into
it, the slot NpcStats field 8 occupies under the five field↔stat identities #797 pinned — and `SimpleChar_t`'s
model build (virtual slot 36, `FUN_01104750`) consumes it:

```
if (GetStat(0x19C) != 0) {
    name = monsterData->spawnanim.empty() ? monsterData->defaultidleanim   // +0x13C
                                          : monsterData->spawnanim;        // +0x158
    if (ResolveAnim(name, &length)) PlayAnim(..., 0x80, ...);              // channel "Control/Anim"
    SetStat(0x19C, 0);                                                     // ONE-SHOT: consumed and cleared
}
```

[V1: tools/ghidra/DumpSpawnAnimationConsumer.java]

⛔ **This overturns #794's refutation.** That argument was: `spawnanim` is **rare** (174 of the 3,185 shipped
MonsterData records declare one, on undead, demons and wraiths — creatures that claw out of the ground) while of
the templates the corpus flags, **275 wear a creature that declares none** against 14 that do — so the flag lands
on Citizens and Guards that have no entrance animation to play. The inference does not follow: the client takes
`defaultidleanim` for exactly those, in an explicit branch on whether the `spawnanim` string is empty
(`[monsterData+0x168] != 0` is its `_Mysize`). A flagged Citizen replays its idle, which is invisible, not wrong.
The same window identifies the object the name is read from, by taking three neighbouring defaults from it —
`weaponmaterialtype` → `EmitterMaterial`, `materialtype` → `ReceiverMaterial`, `collisionradius` → `CharRadius`,
the offsets the MonsterData loader `FUN_010005E0` writes them to.

⭐ **And the SENDER sets it at (re)creation** (#803). The discriminating axis is not in any spawn body: it is
whether a **respawn re-arms the flag**. `--report spawn-animation`'s respawn test matches each captured death
(`BehaviorControlCenterIIR` BehaviorId `0x1F` phase `0x0004`) to the same template coming back within 15 m of where
it fell, later in the same capture — a positional match, because retail recycles a handle, so a respawned creature
is a different entity by every id it carries.

| | |
|---|---|
| respawns matched | **35** (272 deaths; 8 actors never spawned in the dump, 229 with no later spawn at the spot) |
| flagged | **20** = 57.1%, against a flat first-sighting base rate of 9.5% |
| **per-template control** | **8.1 expected**, 20 observed |

⚠️ The control is the load-bearing half, and it has to **exclude the matched respawns from the control
population**. Leaving them in is circular in the direction that hides the effect: 13 of the 15 flagged `Hatchling`
first-sightings *are* these respawns, so an uncorrected control credits the hypothesis' own evidence to chance.
The cleanest lines are the solitary animals, where a 15 m match cannot pick up a pack-mate standing nearby:

```
creature          template   respawns SET   OTHER first-sightings SET
Panther           9590            3/3            1/68     1.5%
Wild Cow          14467           2/2            0/147    0.0%
Wild Pig          14466           1/1            0/83     0.0%
Brutal Pirate     12130           6/9           13/78    16.7%
```

⚠️ **What that does NOT settle is the CLEAR.** The gap it measures is death → *delivery*, and a spawn body arrives
when the player is in range, not when the creature respawned — so an unknown respawn timer sits inside every gap
and no flagged delivery bounds the window from below. "Cleared once delivered" and "a flat time window" both
survive; separating them needs a recipient, and the corpus cannot name one (`CapturedPacket.Port` is the game
side's port, so re-keying the stickiness test by it is a no-op).

**What we send.** `WorldBroadcaster.BroadcastNpcRespawn` stamps the flag onto the respawn delivery
(`NpcSpawnBuilder.WithSpawnAnimation`, a parse→set→build re-encode rather than a byte patch, since the field sits
inside the embedded protobuf); no other path sets it. This deliberately **under-sends** — retail also flags the
~6-9% of ordinary first-sightings whose creature respawned before the player arrived — because reproducing those
needs the window. It is also what makes the rule safe here: #803's own warning was that a rule keyed to "created
recently" would flag *every* NPC in the zone on the first login after a server start, since our world builds them
all at load. A respawn is a distinct event from world-load creation, so keying on it cannot do that. The
mode-keeping above stays an approximation of the per-entity state, and rides underneath.

⚠️ **The correction used to live inline in the placed-NPC path, so it only ever reached placed NPCs.** Pets,
companions and mounts are lifted by their own importers and have **no `NpcSpawns` placement at all** — they are
summoned, not placed — so every one of them kept its representative's pose permanently. That is why 42 stat
cores had no spawn and none of them was ever revisited (#689). The tally is now shared and runs on all three
paths.

There is a second reason those rows froze, and it is worth knowing: the pet/mount importers keep **one
representative per NAME**, and several creature names cover more than one template — **four** Courtesans, two
Cobras, two Chickens, two Bulls, two Drunk Pirates. The siblings the dedup shadows are lifted by nothing and
placed by nothing. `PerSpawnEventModes.CorrectObservedTypes` therefore corrects every template the sweep saw a
sighting of, not only the ones a caller lifted — which is what moved Courtesan 11479 off `BccState` 50 (its own
sightings are 1×10, 50×1) onto 1.

**What an orphan core is, and why it stays.** Of the 42, **35 are summon targets** (pets, companions, minions —
referenced by NAME from `SpellEffects.Template`), **one is an encounter boss** (Eerika of the Maidens, swapped
in by #502), and **eight of the summon targets are also ambient-area members** (Sheep in 56 areas, Cobra 33,
Chicken 18, Panther 15, Dog 9) spawned by area pools rather than placements. None of that is a dropped
placement — they are entities the game never places, and deleting their cores would break pets and the Damsel
encounter.

**Residue: one row.** `Drunk Pirate` **7383** ships `BccState` 41 / idle 41297 where its same-named twin 9327
sits on 1 / 42024 — and the current corpus has **no sighting of 7383 at all**, so no re-harvest can correct it.
That is the ticket's title, for the right reason at last. `NpcStatCoreColumnsTests
.BccStateAndIdleAnimation_AreNotAPoseTheTypeIsRarelyCaughtIn` now covers unplaced types too; it used to exclude
anything without an `NpcSpawns` row, which is exactly what hid all of this.

## Feasibility (verified)

Decoding all 1043 captured stat cores confirmed the lift can be byte-exact:
- **No protobuf extension/unknown fields** on the root IIR or any of `CharacterStats`/`NpcStats`/`CharacterBcc`
  (max ext bytes = 0 everywhere), so a message reconstructed from typed columns re-serialises identically.
- Root `ProtoCoreCharacterIIR` scalars beyond the sub-messages: `Name` (already lifted), `CanFall` +
  `MovementMode` (set on every row), and `PetOwnerInstance`/`PetType`/`PetPoints` (pets only → runtime tier).
- The runtime fields (`Anim*Id`/`Timer` phase, `BccState`, current HP) are emitted **canonical** at spawn, so
  they are NOT stored; `AnimIdleId` (the creature's idle clip) is type-level and lives on `NpcTemplate`.

## Migration status (#244)

Goal: retire `Npc.Data` / `NpcSpawn.Data` and the `SpawnPacketBuilder` splice; construct every NPC spawn
from typed columns.

- [x] `NpcStatCoreColumns` (explicit if-mapping, no reflection): Lift(msg) -> NpcStatCore and
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
      placement fields across all 980 populated spawns (`NpcStatCoreColumnsTests`). `Data` still supplies the
      frame + name + equipment structure.
- [x] `NpcSpawnBuilder.BuildBody` assembles the WHOLE body with no template: header constants;
      transform `2C 0A <vec3> 12 <quat> 18 <token varint>` (token written in-place as a placeholder - the
      live token is re-stamped per-send, see above);
      pre-IIR = `SpawnPreIir.BuildMinimal`; CoreChar header `[41][X][msgLen]` with X = the lifted
      `NpcStatCore.IirHeader` (RE: type-stable, `ReadIirHeaderWord`); ANIMAL post-IIR = `BuildMinimalPostIir`,
      clean-HUMANOID post-IIR = `NpcSpawnAppearance.BuildPostIir(morphs)`.
- [x] `NpcSpawnBuilder.BuildMessageFromColumns` builds the whole `ProtoCoreCharacterIIR` from the DB (Name +
      `ApplyStatCore` + equipment rows). Whole-DB gate (`NpcSpawnFromScratchTests`): **824/988 build byte-exact
      from columns**, 9 canonically cleaner (drop a captured runtime target), 155 the humanoid runtime-post-IIR
      tail (keep `Data`).
- [x] WIRE it into the live path: `TryBuildLive` builds the body via `BuildMessageFromColumns` +
      `BuildBody(zoneToken)` for every expressible case, falling back to the `Data` splice only for the
      residual runtime-post-IIR tail. NB `SendWorldSpawn` STILL re-stamps the playfield token per-send
      (`SpawnTransformPatch`) - the token baked at build is only a placeholder; the live token isn't known
      until the player enters (a 2026-07-08 regression baked the load-time token + dropped the re-stamp,
      stalling world entry).
- [x] #243: **the live path now builds 964/988 shipped spawns from scratch (was 786); only 24 keep `Data`.**
      Two RE'd post-IIR shapes are now constructed from columns:
      - **no-morph NPCs**: the 41-byte minimal post-IIR whose header third word is 1, 2 or 3, and no morph entries.
        That word is the entity's MOVEMENT MODE — the same value the embedded character message carries in
        `movement_mode` — so it is no longer lifted into a column of its own: `NpcSpawnBuilder` reads it off the
        message (`NpcStatCore.MovementMode`) and the two copies cannot disagree. See #605 and
        `docs/re/fa015807-spawn-reader.md`. Verified in-client 2026-07-08 (as `recordCount`): Kalanthes (1) +
        Saddur (2) render with correct head/hair.
      - **runtime-bearing-with-morphs**: a large first record (movement/bounds float triples) with the **morph
        table embedded after it** at `12 + firstRecordLen` (`[count][morphId f32 0 0 strLen str]…` + the shared
        0x3F1 terminator). `NpcSpawnAppearance.TryDecodeAppearanceLenient` skips the runtime record and lifts
        the trailing table → morph rows → `BuildPostIir(morphs)`. (In practice these types also have clean
        captures, so the importer's morph backfill-by-name already covered most; the lenient decode makes the
        buried-table instances self-sufficient.)
      - Byte-exact whole-DB gate (`NpcSpawnFromScratchTests`): **945/988** reproduce their (possibly mid-session)
        capture exactly; the 34+9 that don't are runtime-bearing captures the fresh build DELIBERATELY differs from
        (it drops the accrued runtime state - "construct, don't replay").
      - Residual `Data` tail = the ~24 runtime-bearing NO-morph humanoids whose type has no minimal-form capture
        (fresh recordCount unknown) + the 2 entry-runtime spawns. In-client smoke test of a no-morph humanoid
        (ensure it's not bald) still pending.
- [x] Migrate the remaining `Npc.Community`/`Level` readers to `StatCore`, then DROP the duplicate
      `Npc.Community`/`Level`/`Race`/`Sex` columns (`6ca6061c`). Verified redundant first: 0 rows carried data
      `StatCore` lacked. The last readers (`WorldBroadcaster` ×3, `Faolan.Mcp/DatabaseTools`) now read
      `StatCore`, and the importer's duplicate writes (`ImportOps`, pet/mount importers) are gone. `NpcTemplate` is now
      just `Id`/`Name`/`TemplateId` + its navigations. `NpcStatCore.Sex` is the `Sex` enum (wire code
      `None=0`/`Male=2`/`Female=3`); `NpcStatCore.Race` stays a raw `uint` (creature model-race 0..98 — a
      different domain from the 3-value player `Race` enum).
- [x] **Retire the `Data` column.** Every shipped spawn (988/988) now builds from typed columns, so the
      captured `NpcSpawn.Data` blob was dropped from the table (`Faolan.Core.Database/Tables/NpcSpawn.cs`); the
      importer lifts these rows from the dump body at import time without persisting it. `TryBuildLive` is the
      SOLE spawn-body path — there is no longer a `Data`-splice fallback in `NpcSpawnBuilder`. The residual
      no-morph-humanoid header words noted above were closed outright by #605: the word is the movement mode the
      message already carries, so there is nothing left to derive separately.
- [x] **The appearance classifier stopped throwing away real tables (#672).** `NpcSpawnAppearance.TryLiftLenient`
      declined a whole morph table when any entry carried a non-zero third word, described as "an entry carrying
      runtime state". Three NPCs lost their whole appearance to it and spawned **bald** — Turach (template 951),
      Kincaid (7371) and Valkyrie Skalla (22982) — which is #243's symptom on a case #243's fix does not cover.
      It is not runtime state. Three independent shapes say so:
      - a dense **ordinal**: the carrying entries are a contiguous prefix and their values a permutation of 1..N.
        ⚠️ Not *sorted* — Turach's run is 1/2/3 but Kincaid's is 3/1/2/4, so it does not index the table position;
      - ⭐ every carrying entry is a `*texscript_*` morph — a texture script, never a face/body slider or a hair
        mesh (9 of 26,930 shipped rows). Runtime state would not confine itself to one kind of entry;
      - no single-texscript table ever carries it (0 of 719), which is what an ORDER needs two of.

      `NpcSpawnMorph.Word2` / `NpcMorph.Word2` now carry the field and `NpcSpawnBuilder` emits it, so a lifted
      table still round-trips byte-exact — the condition the widening waited on. Only the never-observed `Flag`
      still vetoes a table. `--report spawn-diff` goes 487 → **489 match** with the three placements re-lifted, and
      no captured table is declined any more. What the order *drives* is unresolved, and it is not simply "the
      layer order": 505 of the 508 multi-texscript tables send zeros.
      [V3: SpawnBuildsFromColumnsTests.ASpawnCarryingWord2_RoundTripsItOntoTheWire]

## Class-specific playfields (the city armories)

A few playfields hold a **different NPC set per character class**. The proven case is the **Conarch Armory
(map 5007)**, the instance behind Conarch Village's `Armory` zone-line door: the two captures that walk into it —
`NecroL80_mage-necromancer_login_gameplay_armory` and `Hakotep_hox_login_gameplay0` — see the same five
quartermaster **posts** occupied by different NPCs.

| post (x, z) | Necromancer | Herald of Xotli |
| --- | --- | --- |
| 263.6, 242.1 | Quartermaster Zahur | Quartermaster Sabola |
| 266.6, 224.2 | Quartermaster Haji | Quartermaster Haji |
| 270.9, 225.9 | Quartermaster Akila | Quartermaster Imogen |
| 272.4, 251.9 | Quartermaster Pancras | Quartermaster Ofir |
| 280.5, 240.3 | Quartermaster Ebo | Quartermaster Chuma |

Both characters are the **mage archetype**, so the split is per **class**, not per archetype. Retail instances the
playfield to match: the `IntendedTeleport` into 5007 carries a nonzero playfield-instance token, unlike an
open-world destination.

**How it is modelled.** `Map.ClassSpecific` marks such a map (and forces `MapInstanceMode.Solo` — two classes cannot
share one zone copy of it), and `NpcSpawn.ClassId` says which class a placement belongs to. `ClassId` is null
everywhere else, which is what keeps every ordinary spawn behaving exactly as before:

- It joins the spawn's unique index **and** its derived key — but only **when set**, so the ~1900 class-agnostic
  spawns keep the ids they already had. Both are required, not optional: the two classes' quartermasters stand on
  *identical* coordinates, so a `(map, position)` key collapses them and one whole set is silently lost.
- The importer's per-type burst-truth election (`ImportOps.BuildBurstTruthSpawns`) keys on `(map, class, name)` and
  stamps each spawn with the class of the capture's own played character (`CaptureCharacterImporter.DetectPlayedClass`).
  A spawn on such a map from a capture whose class cannot be determined is dropped rather than kept class-agnostic —
  null means "every class sees this", which is the bug, not a safe default.
- The `npcs` module's scoped overwrite covers the `(map, class)` pairs a run produced, so importing one class's
  capture never deletes another's copy of the armory.

**Runtime.** `WorldBroadcaster.EnsureZoneNpcs` takes the entering character's class and populates the copy from
`GetNpcSpawns(mapId, forClass)`; class-agnostic rows always belong, class-stamped ones only to their own class.
A class we hold no capture for is a **gap, and is reported as one**: `WorldBroadcaster.DescribeMissingClassImport`
makes the zone-line refuse with an in-game message instead of dropping the player into an empty room (which, on a map
with no captured world objects, would have no door back out) — the same rule as an un-imported vendor.

Khemi (5001) and Tarantia (5013) are the same fixture in the other two cities and carry the flag on that basis;
neither has any import yet, so both simply report the gap for every class.
