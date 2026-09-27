# NPC three-tier architecture (template / spawn-point / live)

NPC spawns are constructed from typed storage, never spliced from a capture: every shipped spawn builds from typed
columns (988/988) via `NpcSpawnBuilder.TryBuildLive`, the sole spawn-body path. See also
`docs/subsystems/mid-session-spawn-blocker.md` for the playfield-token gate a spawn must pass.

## The three tiers

An NPC in the world is three separate concerns; the code keeps them in three separate places:

| tier | store | lifetime | holds |
|------|-------|----------|-------|
| **`NpcTemplate`** (template) | DB (`NpcTemplates`) | permanent | The creature TYPE: the canonical stat core shared by every spawn of that type — `ProtoCoreCharacterIIR` fields that are constant across a type (race/sex, monster mesh/data, senses, profession, faction/community, flags, the animation *blend* flags, …). One row per type, keyed by `TemplateId` (the stable RDB `NpcStats.f4` id — a natural-key catalog like `Item`/`Spell`/`Map`). |
| **`NpcSpawn`** (spawn point) | DB (`NpcSpawns`) | permanent, **static** | WHERE and WHAT should spawn in the world: map + position + rotation + a ref to the `NpcTemplate` (`NpcTemplateId`), plus the per-spawn STATIC config that varies between placements of the same type (level and level-scaled pools, monster scale, voice, faction override). **No gameplay/runtime state.** This table must not change as the world is played. |
| **`WorldEntity`** (live) | **in-memory** (`Faolan/GameServer/World/WorldEntity.cs`) | ephemeral (per session) | The active instance: `Handle`, current `Position`, current HP (`EntityVitals`), behaviour state, `InstanceId`, `OwnerHandle`. Built at spawn, mutated by gameplay, gone on restart. Pets, summons (`OwnerHandle`) and loot bags live ONLY here — they have no `NpcSpawn` row. |

`WorldEntity` carries three components beside its own fields: **`NpcRoute`** (`entity.Route` — the spawn point, the harvested route or wander anchors, the leash-return
state and the respawn countdown; its three position-dependent methods take the position rather than reading the
entity), **`NpcPresentation`** (`entity.Presentation` — chatter and greeting lines, ambient animations and their
timer, the encounter bark/lure state) and **`PlayerKnowledge`** (`entity.Knowledge` — the `Known`/`Confirmed`
handle sets and `KnownObjects` a player's client has been streamed, the quest-id sets, the pending reach goals). Every
entity holds all three; an NPC's `Knowledge` and a player's `Route` are simply empty.

The live tier is deliberately not persisted. Runtime state is ephemeral: on restart every active spawn
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

The runtime/transient fields (animation phase, `BccState`, current-HP) come from the live tier as canonical
values (full HP, idle) — they are not replayed from a capture. A fresh spawn is idle and at full health, not frozen
at whatever moment it happened to be captured in (an NPC captured mid-death does not spawn dead). For an idle
reference capture the canonical values are the captured values, so the byte output is unchanged; only mid-action
captures differ.

`NpcSpawnBuilder.TryBuildLive` (a placed spawn) and `TryBuildFromType` (a pet/summon with no `NpcSpawn` row)
both build this way. **The playfield token must not be baked at build/load time** - the value written at
construction is only a placeholder. The token the client accepts is the live playfield token the entering player
loaded, which does not exist until that player enters the world; a spawn carrying any other token is silently
dropped by the client (it never even polls), so `SendWorldSpawn` re-stamps the token per-send via
`SpawnTransformPatch` from the recipient's live zone token. A baked load-time token stalls every world entry.

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

## The nameplate rank: `npc_type` is Minion / MiniBoss / Boss

`NpcStats.npc_type` (field 14, stored as `NpcStatCore.NpcType`) is the NPC's rank, and the client renders it
as the title line on the nameplate:

| `npc_type` | title | rows | who |
|---:|---|---:|---|
| 1 | `Minion` | 55 | plain creatures — zero vendors, zero conversations, mostly hostile trash |
| 2 | `MiniBoss` | 10 | Eerika of the Maidens, Pict Chief, Torgrim Blackfrost, Arnulf Iceblade — and the Tortage destiny NPCs |
| 3 | `Boss` | 59 | King Conan, Saddur, Ape King, Commander Vakarr, Battlemaster Torgvall, Head Poacher |
| 4 | *(none)* | 897 | ordinary interactive NPCs — 534 vendors, 513 with a conversation |

A `Normalset == 4` short-circuits the whole thing to `Pray` (the critter tier: Badger, Chicken, Hen, Moose)
before the rank is consulted.

This is a code path, not an inference from the roster. The nameplate builder reads a non-stat member —
`[[dynel+0x330]+0x18]` — and DEC/JZ-chains it into the three LDB category-100 tokens; the SimpleChar spawn apply
writes that member from the spawn's own `npc_stats` submessage, alongside four other fields whose destination
stats the client's own name table names (`SuperFamily`, `Normalset`, `NPCProfessionName`, `NPCProfessionType`)
and three more from the sibling `stats` submessage (`LooksPacket`, `HeadMesh`, `BCC`). All seven identities fix
the same protobuf base offset, which is what puts field 14 at the slot being written.
[V1: tools/ghidra/DumpNpcTypeRank.java]

It is not the skull rank — that is `Normalset` (stat 205), pinned separately. And the health ordering
agrees with the ladder (2 runs 98k–937k against 3's up-to-300M) without licensing loot or XP to be derived from
it: a rank title is all the client draws.

Verified in-client on map 1010 with the tiers side by side at one level: Torgrim Blackfrost (2) shows `MiniBoss`,
Mama Bear (3) shows `Boss`, and the type-4 NPCs beside them show no title. Value 2 also holds Delia, Sakumbe and
Valerius, so `MiniBoss` appears over those friendly Tortage quest-givers unless the client gates the title on
hostility somewhere off this path; that is retail's own data.

## One placement, several sightings (`PlacementId`)

A captured spawn is keyed on `MapId` + exact XYZ, so every sighting of a creature that is not exactly where it was
last time mints another placement. Two mechanisms identify the redundant rows, and neither is a distance:

- **A patroller** is identified by its harvested beat. Rows sharing a `PatrolRoute.PlacementKey` are one
  patroller seen in different sessions — 1,045 route-carrying spawns are 812 placements.
- **A stationary NPC** has no beat, and its position is *nearly* stable, so a sighting a few centimetres off
  still mints a row. The test is the handle: two real placements are two entities and retail mints two handles,
  while one NPC harvested twice is one entity at two moments — so a single handle sighted at two of a cluster's
  positions walked between them. `StationaryPlacementResolver`, run by the `npc-placements` module and printed by
  `--report spawn-duplicates`. 9 clusters, 23 rows.

**Proximity proves nothing, and neither does "identical in every other column".** Retail really places same-name
NPCs a metre apart — Black Dragon Guards flanking a Tarantia doorway, a pack of Enraged Boars — and those rows are
identical in every column by construction. Of 191 candidate clusters within 5u, 181 are two real placements. And
"the same capture saw both positions" inverts the answer, because a creature that moves is re-streamed by interest
within one session: read per-capture, the corpus calls three sightings of one Scruffy two real placements; read per
handle, one handle stood on two of them.

Nothing is deleted either way. The redundant rows are the placement's variant pool — what differs between them
is exactly what retail re-rolls per spawn (level, the morph table, all seven equipment slots) — and
`AmbientPoolService` stands up one entity per pool, drawing which row it renders as at random (streamed by
`NpcBroadcaster.MaintainAmbientPools`).

### Where a pool member stands

`AmbientPoolService.FreePoint` walks the area's points for one with 2u of clearance from the live members. When none
has it, it falls back to the least-crowded point (the one whose nearest live member is furthest), never a random one
— a random pick can hand back the very point somebody is standing on (nine pairs of creatures at exactly 0.000u in
one map-4020 dump). With the least-crowded rule a point with somebody standing exactly on it scores 0, so it can only
win when every point is exactly occupied, which needs as many members as points — and a pool at that population is
not topped up. Gated by `AmbientSpawnPlacementTests`.

The fallback is needed even though no area is over-subscribed (`MaxCount` equals the point count exactly on all
3,970 areas): the client's own points can sit closer together than our spacing radius. 91 areas (2.3%) contain 244
such pairs, and in exactly those the greedy walk runs out of clear points 195 spawns short of `MaxCount`. Retail
placed those points that close on purpose, so the answer is to crowd rather than to refuse — the radius is our
heuristic, the point list is the client's data. Open work tracked in Gitea #906.

### Cross-map pairs are two true observations

Both mechanisms key on `MapId`, so two records of one NPC filed under two map ids are never compared — and a map's
sub-areas are separate playfield ids sharing one coordinate space. `--report spawn-duplicates` censuses the class:

| pair | pairs within 5u | of the smaller map | level+health agree | one capture filed both ids |
|---|---:|---|---:|---|
| 1090 Ymir's Pass ↔ 1092 The Amphitheatre of Karutonia | 123 | 53 of 53 | 123/123 | no |
| 2000 Old Tarantia ↔ 2020 Tarantia Common District | 42 | 26 of 258 | 32/42 | 15 spots |
| 4020 City of Tortage ↔ 4022 City of Tortage Night | 10 | 9 of 56 | 4/10 | 5 spots |

A row means "retail streamed this NPC to a client standing in this playfield". Near a sub-area boundary retail
streams the neighbour's NPCs across it, so the two rows are two true observations, and both are kept:
`GetNpcSpawns(mapId)` filters strictly by `MapId` and a player is on one map at a time, so no client receives both,
and deleting either would make an NPC pop out of existence at a district line. The two rows are independent
entities in two zones, so a creature killed on one side still stands for an observer on the other — cosmetic, and
only visible across a boundary.

Do not resolve a spot's "true" map by majority vote over captures: an NPC near a boundary is seen far more often from
outside it than from inside, so the vote counts observers, not placements (it answers `2000` for all 15 Tarantia
spots, 8 captures to 1, against the one capture that walked the district and sighted the Wharf Rat 145 times).

## Ambient idle animations

The spawn body's `AnimIdleId` is the pose a creature holds. What makes it look alive is separate and does not ride
the spawn at all: retail streams a `BehaviorId.Emote` record every so often naming one of the creature's ambient
animations — a citizen leaning on a wall, scratching, looking round.

Those animations are type-level, stored per creature as `NpcAmbientAnimation` (`NpcTemplate.AmbientAnimations`)
and harvested from the corpus by the `npc-ambient-animations` module: 767 rows over 222 creature types, 116 distinct
animations. A type no capture ever caught idling has none and stands still — a borrowed animation may not even exist
on another creature's skeleton.

Each row carries the animation's own length (the record's `activation_time`, which the client is told) and how often
retail was captured playing it. `WorldState.Tick` picks one weighted by that count while the NPC is genuinely
standing — not chasing, walking a beat, following or engaged, which is retail's own split (24,432 of the 25,750
attributable captured records land on an actor with no MoveTask in the preceding 10s) — and schedules the next for
after this one finishes plus a short pause. See the ambient section of
[behaviorcontrolcenter-grammar.md](../re/behaviorcontrolcenter-grammar.md) for the measurements and
`BehaviorAnimations.AmbientEmote` for the record.

## NPC-follows-NPC: the ambient formation

Two NPCs that look like they chase each other (the annotation *"giner & scruffy should be chasing each other"*) are
not a chase and not mutual on the wire: `Ginger` gets an ordinary multi-waypoint path and `Scruffy` gets a `MoveTaskIIR` whose task
is a **FormationMoveTask** naming Ginger's handle, and Scruffy's own from-position lands within 1.3–3.1 units of
Ginger's every time. One creature leads; the other is bound to it.
[V2: NecroL80_mage-necromancer_login_gameplay_annotated_1.tshark.json frames 1349 / 1521 / 2500 / 2836]

The record's six words are `[class][leaderHandle][offsetX][offsetY][offsetZ][slot]`
([movetask-grammar.md](../re/movetask-grammar.md)). 170 of them over 21 captures give 8 creature-type pairs:

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

They are stored as `NpcFollowPair`, keyed by the type at both ends and harvested by the `npc-follow-pairs` module
(`--report follow-pairs` prints the survey). Both ends, not a `FollowsTemplateId` column on the follower: `Slave of
Acheron` was captured behind two different leader types, and a single column would have to pick one.

**The offset is per-instance and is deliberately not stored.** The four `Black Dragon Guard Patroller`s behind
one captain each hold a different corner of a 3×3 grid with their own slot index; stamping one of them on the type
would stack every follower of that type in the same spot. The runtime derives its own slot from the follower's
handle instead — the same deterministic fan-out a player's pets already use.

At zone load `ZonePopulationLoader.BindFormationFollowers` binds each follower to the nearest live leader of a type
it walks with, within `FormationBindRange`. Nearest matters: a map carries several groups of the same two types
(five separate `Nemedian Deathstalker` groups in one capture, four `Ginger`/`Scruffy` pairs across three), so
binding to "any leader of that type" would march a follower across the zone. The range is ours — the corpus
states the pairing and never a maximum distance — so it is set at the leash territory a patroller already works in.

`WorldState.Tick` then runs the companion-follow step, whose leader may be a player or an NPC. A leader that dies
or leaves the zone releases its followers.

**Not modelled:** re-binding when a leader respawns (the follower simply keeps its own behaviour until the next
zone load), and chains (nothing in the corpus has a follower that is itself a leader).

## The four per-spawn-event fields, and who corrects them

`MovementMode`, `npc_stats.SpawnAnimation`, `Stats.BccState` and `Bcc.AnimIdleId` are rolled per spawn
event, not properties of the type. A type's core is lifted from ONE representative sighting, so whatever that
NPC happened to be doing at capture time would become the type's definition of itself and be inherited by every
placement. `PerSpawnEventModes` tallies each field over all of a type's sightings and keeps the mode.

It is not a blanket reset to a resting value: 14 types are never captured in `BccState` 1 — the Door Warden
(19619) is state 11 in all 15 of its sightings — so a type *can* rest somewhere other than the common value.

**`SpawnAnimation` is a decaying state, not merely per-event.** Over the 4,687 entities sighted more than
once, 351 change their answer, 346 of those are on for the first sighting and off thereafter, and not one ever
turns on (`--report spawn-animation`).

**The client says what the state is a state of: "you have not played your entrance animation yet".**
The wire field lands on client stat `0x19C SpawnAnimation` — `FUN_01458d40` copies `npc_stats+0x24` straight into
it, the slot NpcStats field 8 occupies under the five field↔stat identities of the nameplate-rank section — and `SimpleChar_t`'s
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

`spawnanim` is rare (174 of the 3,185 shipped MonsterData records declare one, on undead, demons and wraiths —
creatures that claw out of the ground), and of the templates the corpus flags, 275 wear a creature that declares
none against 14 that do. That is not evidence against the reading: the client takes `defaultidleanim` for exactly
those, in an explicit branch on whether the `spawnanim` string is empty
(`[monsterData+0x168] != 0` is its `_Mysize`). A flagged Citizen replays its idle, which is invisible, not wrong.
The same window identifies the object the name is read from, by taking three neighbouring defaults from it —
`weaponmaterialtype` → `EmitterMaterial`, `materialtype` → `ReceiverMaterial`, `collisionradius` → `CharRadius`,
the offsets the MonsterData loader `FUN_010005E0` writes them to.

**The sender sets it at (re)creation.** The discriminating axis is not in any spawn body: it is
whether a **respawn re-arms the flag**. `--report spawn-animation`'s respawn test matches each captured death
(`BehaviorControlCenterIIR` BehaviorId `0x1F` phase `0x0004`) to the same template coming back within 15 m of where
it fell, later in the same capture — a positional match, because retail recycles a handle, so a respawned creature
is a different entity by every id it carries.

| | |
|---|---|
| respawns matched | 35 (272 deaths; 8 actors never spawned in the dump, 229 with no later spawn at the spot) |
| flagged | 20 = 57.1%, against a flat first-sighting base rate of 9.5% |
| per-template control | 8.1 expected, 20 observed |

The control is the load-bearing half, and it has to exclude the matched respawns from the control
population. Leaving them in is circular in the direction that hides the effect: 13 of the 15 flagged `Hatchling`
first-sightings *are* these respawns, so an uncorrected control credits the hypothesis' own evidence to chance.
The cleanest lines are the solitary animals, where a 15 m match cannot pick up a pack-mate standing nearby:

```
creature          template   respawns SET   OTHER first-sightings SET
Panther           9590            3/3            1/68     1.5%
Wild Cow          14467           2/2            0/147    0.0%
Wild Pig          14466           1/1            0/83     0.0%
Brutal Pirate     12130           6/9           13/78    16.7%
```

**What that does not settle is the clear.** The gap it measures is death → *delivery*, and a spawn body arrives
when the player is in range, not when the creature respawned — so an unknown respawn timer sits inside every gap
and no flagged delivery bounds the window from below. "Cleared once delivered" and "a flat time window" both
survive; separating them needs a recipient, and the corpus cannot name one (`CapturedPacket.Port` is the game
side's port, so re-keying the stickiness test by it is a no-op). Open work tracked in Gitea #803.

**What we send.** `NpcBroadcaster.BroadcastNpcRespawn` stamps the flag onto the respawn delivery
(`NpcSpawnBuilder.WithSpawnAnimation`, a parse→set→build re-encode rather than a byte patch, since the field sits
inside the embedded protobuf); no other path sets it. This deliberately under-sends — retail also flags the
~6-9% of ordinary first-sightings whose creature respawned before the player arrived — because reproducing those
needs the window. It is also what makes the rule safe: a rule keyed to "created recently" would flag every NPC in
the zone on the first login after a server start, since our world builds them all at load. A respawn is a distinct event from world-load creation, so keying on it cannot do that. The
mode-keeping above stays an approximation of the per-entity state, and rides underneath.

The tally is shared by all three import paths — placed NPCs, and pets/companions/mounts, which their own importers
lift and which have no `NpcSpawns` placement at all (summoned, not placed), so a placed-only correction leaves them
in their representative's pose permanently.

The pet/mount importers keep one representative per name, and several creature names cover more than one
template — four Courtesans, two Cobras, two Chickens, two Bulls, two Drunk Pirates. The siblings the dedup shadows are lifted by nothing and
placed by nothing. `PerSpawnEventModes.CorrectObservedTypes` therefore corrects every template the sweep saw a
sighting of, not only the ones a caller lifted — — e.g. Courtesan 11479 lands on `BccState` 1 (its own sightings are 1×10, 50×1), not 50.

**What an orphan core is, and why it stays.** 42 stat cores have no spawn. 35 are summon targets (pets, companions, minions —
referenced by name from `SpellEffects.Template`), one is an encounter boss (Eerika of the Maidens, swapped in by the Damsel encounter; open work tracked in Gitea #502),
and eight of the summon targets are also ambient-area members (Sheep in 56 areas, Cobra 33,
Chicken 18, Panther 15, Dog 9) spawned by area pools rather than placements. None of that is a dropped
placement — they are entities the game never places, and deleting their cores would break pets and the Damsel
encounter.

**Residue: one row.** `Drunk Pirate` 7383 ships `BccState` 41 / idle 41297 where its same-named twin 9327 sits on
1 / 42024 — and the corpus has no sighting of 7383 at all, so no re-harvest can correct it. The
`npc-pose-check` report covers unplaced types too; a check restricted to types with an `NpcSpawns` row hides every
one of these.

## Why the lift is byte-exact

Decoding all 1043 captured stat cores:
- No protobuf extension/unknown fields on the root IIR or any of `CharacterStats`/`NpcStats`/`CharacterBcc`
  (max ext bytes = 0 everywhere), so a message reconstructed from typed columns re-serialises identically.
- Root `ProtoCoreCharacterIIR` scalars beyond the sub-messages: `Name` (already lifted), `CanFall` +
  `MovementMode` (set on every row), and `PetOwnerInstance`/`PetType`/`PetPoints` (pets only → runtime tier).
- The runtime fields (`Anim*Id`/`Timer` phase, `BccState`, current HP) are emitted canonical at spawn, so
  they are not stored; `AnimIdleId` (the creature's idle clip) is type-level and lives on `NpcTemplate`.

## How the body is composed (code map)

- `NpcStatCoreColumns` (explicit if-mapping, no reflection): `Lift(msg)` → `NpcStatCore` and
  `ToStats`/`ToNpcStats`/`ToBcc` back, byte-exact across all 3097 captured sub-messages. `NpcStatCore` is the 1:1
  type-level side table (`NpcStatCores`); the importer lifts it from a representative spawn (`UpsertNpcTypes`), and
  `CapturePetImporter` for summon-only pets. `NpcTemplate` itself is `Id`/`Name`/`TemplateId` + its navigations.
  `NpcStatCore.Sex` is the `Sex` enum (wire code `None=0`/`Male=2`/`Female=3`); `NpcStatCore.Race` stays a raw `uint`
  (creature model-race 0..98 — a different domain from the 3-value player `Race` enum).
- Per-placement stat columns on `NpcSpawn` (`Life`/`Mana`/`MaxMana`/`Stamina`/`MaxStamina`/`ApprenticeLevel`/
  `VoiceSet` non-null; `GuildId`/`HearingSkill`/`SightSkill` nullable) override the type core per placement (a spawn
  point carries its own level; the pools track it). Pools are ~91% `Health*100` but deviate per creature, so they are
  stored.
- `NpcSpawnBuilder.ApplyStatCore` composes the CharacterIIR stat core from `row.Npc.StatCore` (type) + `row`
  (placement overrides), with runtime anim/state canonical; `BuildMessageFromColumns` builds the whole
  `ProtoCoreCharacterIIR` (Name + `ApplyStatCore` + equipment rows).
- `NpcSpawnBuilder.BuildBody` assembles the whole body with no template: header constants; transform
  `2C 0A <vec3> 12 <quat> 18 <token varint>` (the token a placeholder, re-stamped per send); pre-IIR =
  `SpawnPreIir.BuildMinimal`; CoreChar header `[41][X][msgLen]` with X = the lifted `NpcStatCore.IirHeader`
  (type-stable, `ReadIirHeaderWord`); animal post-IIR = `BuildMinimalPostIir`, humanoid post-IIR =
  `NpcSpawnAppearance.BuildPostIir(morphs)`.
- **No-morph NPCs**: the 41-byte minimal post-IIR whose header third word is 1, 2 or 3, and no morph entries. That
  word is the entity's movement mode — the same value the embedded character message carries in `movement_mode` —
  so `NpcSpawnBuilder` reads it off the message (`NpcStatCore.MovementMode`) and the two copies cannot disagree (see
  `docs/re/fa015807-spawn-reader.md`). Verified in-client: Kalanthes (1) + Saddur (2) render with correct head/hair.
- **Runtime-bearing-with-morphs**: a large first record (movement/bounds float triples) with the morph table
  embedded after it at `12 + firstRecordLen` (`[count][morphId f32 0 0 strLen str]…` + the shared 0x3F1 frame).
  `NpcSpawnAppearance.TryLiftLenient` skips the runtime record and lifts the trailing table → morph rows →
  `BuildPostIir(morphs)`.
- Whole-DB gate (`NpcSpawnFromScratchTests`): 945/988 reproduce their (possibly mid-session) capture exactly; the
  rest are runtime-bearing captures the fresh build deliberately differs from (it drops the accrued runtime state).
- **The morph entry's third word (`Word2`) is an order, not runtime state.** Three independent shapes say so: the
  carrying entries are a contiguous prefix and their values a permutation of 1..N (not sorted — Turach's run is
  1/2/3, Kincaid's 3/1/2/4, so it does not index the table position); every carrying entry is a `*texscript_*` morph —
  a texture script, never a face/body slider or a hair mesh (9 of 26,930 shipped rows); and no single-texscript table
  ever carries it (0 of 719), which is what an order needs two of. A lifter that declines such a table spawns the
  NPC bald (Turach 951, Kincaid 7371, Valkyrie Skalla 22982). `NpcSpawnMorph.Word2` / `NpcMorph.Word2` carry the
  field and `NpcSpawnBuilder` emits it; only the never-observed `Flag` vetoes a table. What the order drives is not
  decoded, and it is not simply "the layer order": 505 of the 508 multi-texscript tables send zeros.
  [V3: SpawnBuildsFromColumnsTests.ASpawnCarryingWord2_RoundTripsItOntoTheWire]

Open work on NPC appearance tracked in Gitea #672 and #766.

## Class-specific playfields (the city armories)

A few playfields hold a different NPC set per character class. The proven case is the Conarch Armory
(map 5007), the instance behind Conarch Village's `Armory` zone-line door: the two captures that walk into it —
`NecroL80_mage-necromancer_login_gameplay_armory` and `Hakotep_hox_login_gameplay0` — see the same five
quartermaster posts occupied by different NPCs.

| post (x, z) | Necromancer | Herald of Xotli |
| --- | --- | --- |
| 263.6, 242.1 | Quartermaster Zahur | Quartermaster Sabola |
| 266.6, 224.2 | Quartermaster Haji | Quartermaster Haji |
| 270.9, 225.9 | Quartermaster Akila | Quartermaster Imogen |
| 272.4, 251.9 | Quartermaster Pancras | Quartermaster Ofir |
| 280.5, 240.3 | Quartermaster Ebo | Quartermaster Chuma |

Both characters are the mage archetype, so the split is per class, not per archetype. Retail instances the
playfield to match: the `IntendedTeleport` into 5007 carries a nonzero playfield-instance token, unlike an
open-world destination.

**How it is modelled.** `Map.ClassSpecific` marks such a map (and forces `MapInstanceMode.Solo` — two classes cannot
share one zone copy of it), and `NpcSpawn.ClassId` says which class a placement belongs to. `ClassId` is null
everywhere else:

- It joins the spawn's unique index and its derived key — but only when set, so the ~1900 class-agnostic spawns'
  ids do not depend on it. Both are required, not optional: the two classes' quartermasters stand on
  identical coordinates, so a `(map, position)` key collapses them and one whole set is silently lost.
- The importer's per-type burst-truth election (`NpcImportOps.BuildBurstTruthSpawns`) keys on `(map, class, name)` and
  stamps each spawn with the class of the capture's own played character (`CaptureCharacterImporter.DetectPlayedClass`).
  A spawn on such a map from a capture whose class cannot be determined is dropped rather than kept class-agnostic —
  null means "every class sees this", which is the bug, not a safe default.
- The `npcs` module's scoped overwrite covers the `(map, class)` pairs a run produced, so importing one class's
  capture never deletes another's copy of the armory.

**Runtime.** `ZonePopulationLoader.EnsureZoneNpcs` takes the entering character's class and populates the copy from
`GetNpcSpawns(mapId, forClass)`; class-agnostic rows always belong, class-stamped ones only to their own class.
A class we hold no capture for is a gap, and is reported as one: `WorldBroadcaster.DescribeMissingClassImport`
makes the zone-line refuse with an in-game message instead of dropping the player into an empty room (which, on a map
with no captured world objects, would have no door back out) — the same rule as an un-imported vendor.

Khemi (5001) and Tarantia (5013) are the same fixture in the other two cities and carry the flag on that basis;
neither has any import, so both report the gap for every class. Open work tracked in Gitea #500.
