# Player spawn generation (reference)

The byte-level derivation reference for the player's own `SimpleCharFullUpdate` (0xFA015807) spawn, generated
entirely from DB character data — name, race, sex, level, vitals, full equipment rendering and the surrounding
scaffold frame. The verified per-message index is [`../protocol/gen/messages.md`](../protocol/gen/messages.md)
(`SimpleCharFullUpdateIIR`); the executable spec is the codec (`SimpleCharFullUpdateCodec`), golden-tested.
Captured bytes survive only as test oracles (e.g. the `golden/SpawnAssassinTemplate.bin` entry of
`Faolan.Tests/Assets/fixtures.bin`). The wire grammar parses and regenerates every captured player-own spawn
byte-exact (`tools/capture/parse_spawn_scaffold.py`; C#: `SpawnFeatListTests`, `SimpleCharFullUpdateCodecTests`,
`PlayerSpawnScaffoldRowsTests`).

## Architecture

World entry (`WorldEntryService.BuildBurstlessPlayerSpawn`) builds the player's own spawn with
`PlayerSpawnBuilder.BuildFromScratch`, which assembles a typed `SimpleCharFullUpdateIIR` and serialises it through
`SimpleCharFullUpdateCodec`:

1. **Transform** — handle, playfield token, position and rotation (a near-zero stored quaternion falls back to
   identity).
2. **CharacterIIR** — `PlayerCharacterIirs.ForCharacter`: a fixed render template with the character's
   name/sex/race/level/vitals/looks applied and the worn equipment from the character's `Equipped` `ItemInstance`
   rows via `PlayerSpawnEquipment` (`InventoryEntry { pos, ProtoAcgItem }`, items built like `SnapshotItems`).
3. **Scaffold frame** — `PlayerSpawnScaffoldRows` regenerates everything around the CharacterIIR from the
   character:
   - the **appearance table is derived, not stored** (`PlayerSpawnScaffoldRows.BuildAppearanceEntries`):
     weighted elements from `CharacterBodyResource` through the `MorphTarget` registry (face paint first —
     retail's slot 1), morph elements from `CharacterBody`. `word2` slot ordinals are `1..n` in that order. The
     registry reaches the builder as a parameter (`IDatabaseRepository.GetMorphTargetRegistry`, read once per
     process — 1,072 rows of static seed content);
   - `CharacterSkill` — the skill list (`SkillId` + `Rank`, the client's `[u32 id][u8 rank]` pairing); the
     archetype ability that opens it (archetype × 100) and the class-progression entry are derived per send;
   - `CharacterScaffoldPair` — the two ending `[id][s32]` pair lists (`ListIndex` 0/1);
   - the pre-IIR frame is constructed from `Character.Class` (`PlayerSpawnScaffoldRows.FreshPreFrame`) — it
     carries no per-character data beyond the class, the vanity mask and the feat specs.

A character with no scaffold rows throws; the rows are seeded once at creation
(`CharacterCreationService.SeedSpawnScaffold` → `IDatabaseRepository.SaveCharacterScaffold`) with the
fresh-character defaults in `PlayerSpawnScaffoldRows`: the progression a character starts with (feat/skill ids, an
empty spec, the fresh pre-frame). The appearance is not among them — it is derived from `CharacterBody` +
`CharacterBodyResource` on every send. Locked by `FreshRows_OverDikketestBody_AreByteIdenticalToTheCapture`,
which reproduces Dikketest's captured spawn from his own appearance model.

**Retail's entry indices are per-character slot ids, not positions** — they survive appearance edits, so a
re-barbered character carries gaps and appends its re-chosen resources after its morphs (`NecroL80` runs 2..15,
`DemoL80` runs 4..13 then 24,23). Nothing needs to derive them: every character we build spawns with `1..n` and
renders. Those two captures round-trip to the same `(tag, value)` multiset with a canonical index run —
`EditedAppearance_RoundTripsExceptItsRetailSlots` — while `SpawnAssassinTemplate` / `SpawnDikketestLogin` /
`SpawnHeraldBuffed` stay byte-exact through the derivation.

Buff/timer records are transient session state and are not stored: a buffed captured spawn extracts to the same
rows as its unbuffed form (`BuffedSpawn_RoundTripsToItsUnbuffedConstructedForm`).

The player spawn is the most fragile packet — a wrong rotation can hang the client on the loading screen — so
changes are gated on an in-client smoke test.

## Proven building blocks

- The char message inside the spawn body is a **`ProtoCoreCharacterIIR`** and decodes + re-encodes
  **byte-identically** (`PlayerSpawnGenerationTests`).
- **The equipment lives inside it**: `ProtoCoreCharacterIIR.InventoryEntries` (field 23, repeated
  `InventoryEntry { pos, item: ProtoAcgItem, gem_slots[] }`); `ProtoAcgItem { key1/2/3: ProtoDecryptionKey
  (= Item.Data1..4), template1/2 = Item.Id, hash = archetype, level }`.
- **`pos` → `CharacterEquipmentSlot`** — all 17 retail slots: Neck=1, Head=2, **Tokens=3**, Chest=5,
  MainWeapon=6 (main_hand_LEFT), Belt=7, **OffHandWeapon=8** (Ceremonial Dagger), LeftRing=9, RightRing=10,
  Cape=11, Shoulder=12, Boots=14, Hands=15, Legs=16, Wrist=18, **RangedWeapon=20** (Executioner Crossbow),
  **Ammo=21** (Bolt Quiver); gaps at 4/13/19, bag=17. The melee pair is 6/8 (left navs), the ranged pair 20/21
  (right navs).

## Wire grammar

Workbench: `tools/capture/parse_spawn_scaffold.py` (`template` / `players` / `all` / `one` / `dumpb` / `pre` /
`prediff` commands). C#: `SimpleCharFullUpdateCodec`.

- `[u32 bodyLen=len-4][u32 FA015807][u32 ack=C350][u32 handle][u32 fieldA]`
- transform group `0x2C`: `0A <len> vec3, 12 <len> quat, (25 <fixed32 scale>,) 18 <varint playfield>`
- **pre-IIR frame** (between the transform and the CharacterIIR length prefix) — `SpawnPreIir` /
  `SpawnPreIirCodec`. It is the same `SimpleCharFullUpdateIIR_t::ReadSubClass` scaffold the NPC spawn uses
  (grammar in `docs/re/fa015807-spawn-reader.md`), plus the head of the embedded CoreCharacter:

  ```
  [u8 version 0x05] [6 x u8 + 53 x u32, in the reader's fixed order, around one count-framed SUBLIST]
  [u8 CoreChar version 0x41] [u32 CoreChar IirHeader]
  ```

  The last two fields make the frame appear to end in a magic `41 00 00 82 08|0a` "closer": a parse that cuts at
  the CharacterIIR's u32 length prefix cuts 5 bytes into the CoreCharacter's 9-byte header
  `[u8 0x41][u32 iirHeader][u32 msgLen]`. The `iirHeader` is the same word `NpcSpawnBuilder` writes from
  `NpcStatCore.IirHeader` (0x8208 or 0x820A; not per-character).

  **The sublist is the character's saved feat specs**, one entry each:
  `[u32 index][u8 index][u16 len + Latin1 name][count-framed list of u32 feat ids]`. Every normal login carries
  exactly one entry with an empty name, and the create-editor preview carries none, which is why its frame is
  exactly `SpawnPreIir.FixedLength` + 5 = 228 bytes. Six captured frames carry two or four named specs
  (`PvE - Lance` 23 feats, `PvP` 27, `PvP - Decap/Protection` 28, `PvP - Decap/Armor` 28, `Hybrid` 27,
  `Berserker: No Decap` 26).

  So the 25 captured frame lengths are one shape parameterised by the sublist, and its "landmark constants" are
  list counts: `0x07E2` = `ForCount(1)` (one spec), `0x03F1` = `ForCount(0)` (that spec has no feats). A feat id
  such as `0x0001adb4` reads as a `[00][01][adb4]` table entry if you look for a table.

  Named fields inside the 53 words: **4** = the vanity shown-slots mask (frame offset 18), **17** = the category
  enum, carrying its modal `0x6f` on all 259 captured frames, **19** = a constant `0x41700000` (float 15.0) on all
  259, **21** = a per-session value (6..44, plus round outliers 1005/1586/9000/11185/14000) that nothing predicts.
  The 6 u8 fields carry the archetype (Soldier 1 / Priest 2 / Rogue 3 / Mage 4) and the class byte at flags 1 and
  2 — the frame's only per-character content besides the mask and the feat specs.

  The u32 at frame offset 133 is **not a length**: Hoxxling's stays 1067 while his post-IIR varies by ±280 bytes,
  AssassinL23's is 2018 (the sublist's `ForCount(1)` word), and several characters have no length-consistent u32
  at all.
- `[u32 innerLen]` + the **ProtoCoreCharacterIIR**.
- **post-IIR stream** (contiguous to EOF; nothing follows it):
  - header: a **plain-counted list** `[u32 n][n x u32]` then `[u32 4]` (`firstRecordLen`). `n` is 0 on all but 2
    captured spawns, which carry a single word (60).
  - `[u32 movementMode][u32 ListFrame]` — the buff list, grouped by spell, in the client's own carve
    (`FUN_0145b8a0`/`FUN_0145c0b0`/`FUN_007ed9b0`): per group `[u32 spellId][u32 ListFrame]` then that many
    52-byte records (a discarded u32 + twelve fields: timer pair, caster level, the
    `[EntityClass.Character][handle]` reference to whoever the buff is on). Every captured group holds one
    record. An empty list frames as a bare `0x3F1`, which is `ForCount(0)`.
  - **the entry slot** — one grammar for players and NPCs (`SpawnEntrySlot`), decided by the client's read order
    (`FUN_010087b0`/`FUN_0079cfd0`, see "Where the client reads the scaffold lists" below):

    ```
    [u32 count]  count × [u32 key][f32 value][u32 word2][u16 nameLen][name]   the APPEARANCE table
    [u32 ListFrame][n × [u32 id][u8 rank]]                                    the SKILLS
    [u32 ListFrame][m × [u32 50000][u32 inst][u32 slot][u8 kind]]             the PETS
    ```

    Each appearance element goes to `MorphValues_c::SetMorphValue(key, value, word2, name)`; the name is empty
    on every captured element (the key carries the identity), so the element is 14 bytes. Weighted elements
    carry `value` = the registry weight (1.0), morph elements the slider float. `word2` is a per-character slot
    id retail keeps across appearance edits, so DemoL80's re-chosen hair/beard sit at 24/23 after its morphs. An
    NPC's slot is the same grammar with empty skill and pet lists.
  - **the skills** = `CharacterSkill`, `[u32 id][u8 rank]` per element (the client stores `map[id] = rank`).
    Every captured player's list opens with its archetype ability (100/200/300/400 = archetype × 100, rank 0 on
    all 296) and ascends from there. The last element's rank is nonzero on 28 captured spawns. On a character
    with 65+ skills the frame word overflows 16 bits (seven captured values satisfy `(0x10000|tag) % 0x3F1 == 0`,
    framing 71..120 skills).
  - **the pets**: the second `ListFrame`'d list — see below. 17 captured spawns carry pets
    `ForCount(1)`/`ForCount(2)`; the rest carry `0x3F1` = `ForCount(0)`.
  - ending: two **plain**-counted pair lists `[u32 N][N x (u32 id, s32 value)]` = `CharacterScaffoldPair` (ids
    are decades 10,20,...,110; DemoL80 has both lists non-empty), then a third pair list that is
    **count-framed** (`[u32 ListFrame word][n x (u32 id, s32 value)]`).

A single `0x3F1`/`0x7E2` word whose list is empty or single-element in most captures reads as a closing constant;
five separate places in this stream are list counts of that kind.

### The 13-byte tail records are the character's pets

| step | function |
|---|---|
| the list | `FUN_0145ba10` — `ListFrame`'d, count capped at `0x7531` |
| the element | `FUN_014578e0` — three u32s and a byte into a 16-byte struct |
| the apply | `FUN_01458d40` (the spawn apply) walks that vector with **stride 0x10** and hands each record to `FUN_0081b960` |
| the object | `FUN_0081b960` allocates **`BasePet_c`** (RTTI `.?AVBasePet_c@@`, 0x28 bytes) |
| the register | `FUN_0081b670` — its own assert cites **`CoreCharacter\Pets\petholder.cpp`** with parameter `i_pcNewPet`, and it logs `"PetHolder_c::RegisterPet: Pet registered (instance %i)!"` on channel `GameCode/Pets` |

```
[u32 50000][u32 PetInstance][u32 SlotSelector][u8 Kind]
     +0x18        +0x1C           +0x20          +0x0C     <- on BasePet_c
```

`50000` is `EntityClass.Character` and the consumer special-cases exactly that value to resolve the referent, so
the leading pair is the ordinary `(class, instance)` entity reference. `PetInstance` is what the log line prints
and what `RegisterPet` de-dupes on (it compares `+0x18` and `+0x1C` against the pet already held).

**`SlotSelector` is not a constant.** `RegisterPet` tests `pet->+0x20 == 1` and puts that pet in the holder's
single slot at `+0x4c`, releasing whatever was there; anything else joins the general list. It is 0 on every
captured spawn because no captured character had a primary pet out.

`Kind` (0x01 / 0x04 in the captures) is `BasePet_c`'s own field at `+0x0C` and is unnamed.

### The third pair list is a stat list

The 8-byte `(id, value)` shape is shared by **two unrelated families**. Lists 1 and 2 are the faction-reputation
block — ids in the decades, values a signed standing. List 3's ids are wire **stat** ids, and every one resolves
in the client's own name table (`docs/re/aoc-client-stat-names.tsv`):

| id | client name | carriers | values | family |
|---|---|---:|---|---|
| `20` | `BCCState` | 8 | 1 | NPC spawns |
| `975` | `DamageAbsorb` | 39 | 0 | both |
| `1351` | `NaturalEnergyRegeneration` | 9 | 5625 / 7368 | **player only** |
| `1000028` | `Energy` | 9 | 3222 / 9000 / 14000 | **player only** |
| `1000029` | `MaxEnergy` | 9 | 9000 / 14000 | **player only** |

**The three energy stats land on the same 9 spawns**, with `Energy <= MaxEnergy` on every one — a pool, its
denominator and its regen rate pushed together. So `MaxEnergy` is on the wire, in this count-framed list.
Over the whole `SimpleCharFullUpdateIIR` corpus the list has **56 carriers and five ids**.
[V3: SpawnPostIirGrammarTests.TheThirdPairList_IsAStatList_NotAThirdFactionList]

The pet `Kind` byte is decoded structurally but not semantically and is carried verbatim.

### The client's readers (V1)

The chain, recovered by decompiling the vtable slot-8 readers (`tools/ghidra/DecompAddrs.java`):

| Function | Is |
|---|---|
| `FUN_01121c00` | `SimpleCharFullUpdateIIR_t::ReadSubClass` (`simplecharfullupdateiir.cpp`), version byte 5 |
| `FUN_01122b10` | the feat-spec sublist — `0x3F1`-framed |
| `FUN_0145ac80` | `CoreCharacterFullUpdateIIR::ReadSubClass` (`CoreCharacter\IIR\CoreCharacterFullUpdateIIR.cpp`), version byte `'A'` |
| `FUN_00cbfb80` | the length-prefixed embedded protobuf |
| `FUN_010087b0` | a plain-counted list, per element `[u32][u32][u32]` + a length-prefixed **string** |
| `FUN_0079cfd0` | a `0x3F1`-framed list, per element `[u32][u8]` (5 bytes) |
| `FUN_0145ba10` | a `0x3F1`-framed list (bound 30001), per element `FUN_014578e0` = `[u32][u32][u32][u8]` = **13 bytes** |
| `FUN_004d5900` | a `0x3F1`-framed list of 8-byte pairs; the client **discards the first u32 and keeps the second** |

The head list is a plain-counted `std::vector<u32>` (built at `+0x13c/+0x140/+0x144`), the 13-byte tail record is
a real framed-list element down to its trailing byte, and the closing pair list is count-framed where the two
before it are plain-counted.

**`generic_stats` is positional against a client-side order table.** The reader asserts
`g_Int32StatsOrder.size() == m_CoreCharacterMessage.generic_stats_size()`, and `FUN_01458d40` consumes the two
together — `g_Int32StatsOrder[i]` is the stat id for the value at `generic_stats[i]`. So the protobuf carries
values with no ids, and the ids live in a global the client builds at runtime (`DAT_02d25da4`, a
`std::vector<u32>`; `FUN_01e14b00` is only its destructor, so the population is elsewhere and a static dump yields
nothing). This assert refers to the embedded protobuf message, not to the wire pair lists read before it.

### Where the client reads the scaffold lists

`SimpleCharFullUpdateIIR_t::ReadSubClass` (`FUN_01121c00`, self-named by its own `"...Received a bad stream upon
entering!"` assert and `simplecharfullupdateiir.cpp`) contains **zero loops** — it is a flat sequence of
fixed-field reads, so a counted, variable-length entry table cannot be read there.

The scaffold lists are read by **`FUN_0145ac80`**, which asserts with
`CoreCharacter\IIR\CoreCharacterFullUpdateIIR.cpp` and carries **three** `do` loops. Anchor on the `.cpp` name
rather than on a vtable slot — the asserts name their own translation unit, which is the cheaper route into this
subsystem.

The list readers under it, in read order (the whole post-protobuf sequence is
`FUN_0145b8a0` → `FUN_010087b0` → `FUN_0079cfd0` → `FUN_0145ba10` → two plain pair loops → `FUN_004d5900`):

| function | shape | what it is |
|---|---|---|
| `FUN_0145b8a0` | `ListFrame`-counted, allocates `0x18` per element | the buff groups |
| `FUN_0145c0b0` | a second `ListFrame` count inside it, `FUN_007ed9b0` per element | a group's records |
| `FUN_007ed9b0` | allocates a `BuffInfo_c` (`FUN_00c9cce0(200, 4, 0)` + ctor `FUN_00849770`) and fills it field by field | **a buff** |
| `FUN_010087b0` | plain count; per element 3 × u32 + a `[u16 len]` string (`FUN_0048f840` → the string reader `FUN_00cbf840`) | **the appearance table** — each element handed to `FUN_01008070` |
| `FUN_01008070` | `(container, u32 key, f32 value, u32 word2, 0)` + the name string | **`MorphValues_c::SetMorphValue`** — its own log strings on channel `GameCode/MorphValues`; upserts a 76-byte entry keyed on `key` (vector reserved by `FUN_00ff5b60`, stride `0x4C`) and zeroes rival entries whose category is in the exclusive set at `DAT_01f268b4` (hair/colour-style one-of choices). The category comes from the name (`FUN_01008b90`); an empty name — every captured element — leaves the key as the identity |
| `FUN_0079cfd0` | `ListFrame`-counted, per element `[u32 id][u8 rank]`, stored `map[id] = rank` | **the skills** |
| `FUN_0145ba10` | reads a word, divides by `0x3F1`, calls `FUN_014578e0` per element | the pets list |
| `FUN_014578e0` | `u32, u32, u32, u8` — **13 bytes** | the pet record (`[0xC350][id][slot][kind]`) |

**The client's read order is the authority on field boundaries.** A carve shifted 8 bytes —
`[u32 count][u32 X][f32 1.0][count × [u32 idx][u32 flag][u16 tag][u32 value]]` — consumes the appearance region
byte-exactly too, so a round-trip gate cannot tell the two apart. Three corroborations of the client's carve from
directions the carve argument does not touch:

* all seven 16-bit-overflow tags satisfy `(0x10000 | tag) % 0x3F1 == 0` — exact `ListFrame` words framing
  71..120 skills;
* the skill list opens with the archetype ability (`archetype × 100`) on all 296 captured player slots;
* `MorphTargets` id 183 (`0xB7`) is `*texscript_183875`, the always-sent face paint — the first element, which
  retail puts in slot 1.

`AppearanceEntry` in `SpawnPostIir.cs` carries the full mapping from the shifted carve's fields to the client's.

### `BuffInfo_c` is the same object the MagicBuff apply builds

`FUN_007ed9b0` reads **twelve consecutive u32** and stores them at

```
+0x14  +0x18  +0x1c  +0x20  +0x24  +0x28  +0x2c  +0xa8  +0xac  +0xb8  +0xbc  +0xc0
```

(the 6th and 7th are read into locals first and stored `+0x2c` before `+0x28`, a scheduling artefact — the read
order is still sequential). The object is 200 bytes (`FUN_00c9cce0(200, 4, 0)`), so `+0xC0` is its last field.

**Four of those offsets are exactly the ones the apply path also writes** when a `MagicBuffIIR` lands
(`docs/subsystems/npc-spell-applyspell.md` — `ActivateBuff`→`AddBuffInfo` for the apply itself, `UpdateBuff` =
`FUN_007ee710` for the counter refresh). So the scaffold's buff record and the apply payload are **two encodings
of one client object**:

| offset | what the apply path puts there | position in `FUN_007ed9b0`'s read |
|---|---|---:|
| `+0x24` | wire `descriptor[3]` — the **caster's level** (`AddBuffInfo` arg 4); the counter path refreshes it from `CastingLevel@42` | 5th |
| `+0xA8` | `CounterType@4` (`UpdateBuff`) | 8th |
| `+0xAC` | `Counter@0` — gated `> 0` in `UpdateBuff` ("no counter, nothing to update") | 9th |
| `+0xC0` | `CounterTimeLeft` (`UpdateBuff`); the GUI reads it as remaining time for head-flags-bit-20 spells | 12th |

That is a second, independent population for the apply's unsourced fields: the corpus holds 1,471 parsed buff
records off the character full-update stream, carrying the same object's fields under a different framing.

The client's element is 52 bytes; the 8 bytes a flat 60-byte model adds are the group head — `FUN_0145b8a0` reads
`[u32 spellId]` per outer element and `FUN_0145c0b0` the inner `ListFrame` word. The model matches the client
(`ScaffoldBuffGroup { SpellId, Records }`), the discarded u32 opens each record (our `SourceHandle`), and a group
holding two records reads correctly. The apply path's store blocks anchor the timer pair: `+0x18` receives
duration × stat `0x1D0 DurationModifier` × a constant (the client computes the scaled total the scaffold carries
precomputed) and `+0x1c` the unscaled duration. Both the apply and `UpdateBuff` fill `+0x24` with a level, the same
kind of value the scaffold carries there.

Both framing readers divide by `0x3F1` to recover the count — the `ListFrame` arithmetic the whole image uses — so
the framing is confirmed from the client side, not only from captures.

## Morph mapping

The editor morph sliders are the float-valued appearance elements (`CharacterMorphs.TryApply`, driven by the
create-flow `SetMorphValue` handler):

- **The editor's `SetMorphValue` numeric value is a zigzag-encoded signed slider value** ("arse"=31 means
  −16, "arm"=200 means +100). `CharacterBody` stores the decoded signed value.
- **The spawn morph float is `signedValue / 1000`** (single-precision). All 35 captured slider values across
  Hoxxling (28, all sliders modified) / Dikketest (7) / Strickenen (7) reproduce **bit-exact**.
- **Face sliders**: the name's slot prefix ("33_NoseCrookedRight") is the wire sliderId of the type-0x00
  entry (20 sliders, slots 2..36, one CharacterBody column each).
- **Body sliders**: type-0x01 with fixed ids — arm=0, chest=1, bosom=2, stomach=3, arse=4, thigh=5, leg=6;
  **ears is the odd one out: type 0x00, sliderId 0xFF**. (arm/chest 0↔1 settled by send order; the editor
  always moves them together.)
- **Wire entry order = editor send order**: face by ascending slot, then body alphabetically. Unmoved sliders
  produce no entry (strickenbla, zero morphs: 7 weighted elements and nothing else).

Locked by `CharacterMorphsTests` (Hoxxling's raw editor stream → his 28 wire entries bit-exact) and
`FreshRows_OverDikketestBody_AreByteIdenticalToTheCapture` (Dikketest's raw stream → his captured spawn
byte-for-byte).

## Weighted attributes

The weighted (1.0) elements at the top of the appearance table are the character's **chosen appearance resources**
(hair/beard mesh, hair/eye/skin color, tattoos, scars) — not race/class presets, not progression:

- The client RDB carries a **customization registry: type 1000076 id 0** — 1,072 records
  `[nameLen][name][u32 category][u32 defaultBits][f32 weight][u32 id]` covering all appearance targets:
  the face sliders (category 0, "02_BrowLow"), body sliders (1), and the choosable resources
  (3 hairmesh / 4 beardmesh / 5 piercingmesh / 6 hair color / 7 eye color / 8 skin color / 9+10
  tattoos+scars / 2 face paint).
- **Wire tag = registry id − 1** — for the weighted elements and the morph sliders (one unified id space:
  "02_BrowLow" id 3 → wire 2 = the slot prefix; "arm" id 0x101 → wire 0x100).
- The editor's `SetMorphValue` **resource refs** ("*texscript_1108153" = the chosen options) are the
  weighted-element source: wire order = editor send order with one exception — **category 2 (face paint, the
  always-sent `*texscript_183875`, wire key `0xB7`) moves to the front** (retail's slot 1) — weight = the registry
  weight (1.0). faolanx resolves **7/7 bit-exact** against this client's registry; the other five create
  characters resolve 4-6 of 6, the residuals (e.g. tint_char_blank consistently −4) being live-server↔client
  data-version drift — the same drift visible in the body sliders, where retail's wire
  (arse=0x104/thigh=0x105/leg=0x106, proven across 37 spawns) disagrees with this client's registry
  (arse=0x106/thigh=0x107/leg=0x37). Generation follows the client's own registry, with the retail-proven morph
  table kept for the sliders.
- Implementation: `Faolan.Importer --only rdb-morphs` imports the registry → `MorphTarget` (Id = wire tag);
  the create-flow `RecordBodyResource` records the editor's refs → `CharacterBodyResource` (send order); the
  spawn resolves refs through `PlayerSpawnScaffoldRows.BuildWeightedEntries` on every send. A character wears what
  it chose, nothing, or the build fails: a missing registry and a ref the registry does not know both throw, and
  choosing nothing (or only category 2) yields an empty entry list. There is no substitute face: a captured
  character's hair (e.g. `*hairmesh_208094`) belongs to exactly one per-race/sex list, so it cannot stand in for
  another character. `CharacterCreationService.ReportUnresolvableAppearance` names an unimported ref once, at
  creation. Locked by `WeightedAppearanceTests` (faolanx 7/7) and, against the shipped catalog in both
  directions, by `PlayerSpawnScaffoldRowsTests`.

## IIR looks fields

The CharacterIIR's looks are three `CharacterStats` fields, each **half the editor value** (the same 2×
convention as race/class): `head_mesh` = ChangeHeadMesh/2 (dikketest 2285694 → 1142847), `voice_set` =
ChangeVoice/2 (10 → 5), `monster_scale` = ChangeSize/2 (194 → 97) — verified on all five create captures.
`PlayerCharacterIirs.ForCharacter` sets them from `CharacterBody` (HeadMesh/Voice/Size, raw editor values in the
DB). Locked by `PlayerCharacterMessage_LooksFields_AreHalfTheEditorValues` against the dikketest golden.
Hair/beard mesh, colors, tattoos and scars have no IIR fields — they render from the scaffold's weighted and morph
elements. So a fresh character's appearance is IIR (head/voice/scale) + weighted elements (chosen resources) +
morph elements (sliders).

## FullChar (0x737A6DF9) snapshot

The same approach applies to the FullChar/inventory snapshot; the byte-level reader is
[fullplayer-snapshot-reader.md](fullplayer-snapshot-reader.md).

- **Round-trip oracle**: the snapshot's protobuf root decodes as the recovered `ProtoFullCharacterIIR`
  and re-encodes **byte-identically** (`FullCharIirRoundTripTests`).
- **Frame grammar**: the body after `[subop][c350][instance]` is `[u8 flag=0][u32 BE blobLen]` + the
  **ProtoFullCharacterIIR root** (fields: 1 packed character_tag, 4 expansion_flags ≈0xC0001 (expansion accounts
  only), 5 subscription_flags, 4× field-6 containers in kind order 101/104/112/113, 7, 8) + the tails
  (`FullCharSnapshotSubClass`). `blobLen` is a plain big-endian length equal to the root's length — not a varint.
  The 3 bytes `28 81 06` after it on a level-1 beach capture are the root's own `subscription_flags` field, not a
  constant; a regenerated root of a different length requires rewriting `blobLen` or the client misframes
  (`ReadSubClass Wrong IIR_t version`) — `FullPlayerCharacterCodec`, `SnapshotBlobLengthTests`,
  `BeachSnapshotConstructionTests`.
- Container constants (all captures): max_size 1028/24/48/48 (24 = starter bag, grows with upgrades),
  disabled_slots_count 0/0/12/0, owner = instance. Entry ids: itemId = epoch **0x20D82B63**<<32 | server-counter;
  record id (stat 23) another counter; stat 0 = flags (1 / 0x4000001), stat 26 = stack.
- **Containers**: `FullCharacterRoots` builds the 4 containers from the character's DB items (typed;
  deterministic instance+slot-derived ids), carried items rendered in their containers by kind, pos-ascending
  (`FullCharacterRootsTests`, `BuildContainers_RendersBagItems`; the item model is in
  [inventory.md](../subsystems/inventory.md)). Moves/drops/splits persist
  (`Faolan/GameServer/Inventory/EquipmentService.cs`, `EquipmentPersistenceTests`).
- **Tails**: `FullCharSnapshotSubClass` carries the live level, current vitals and XP; the game-static regions
  bake in via `AccessWindowCatalog`. The progression block before them is a sequence of
  `[u32 field-block][length-prefixed <remoteformat> XML]` records, byte-identical across beach characters except
  the instance.

See `docs/subsystems/world-entry-flow.md` for the surrounding world-entry sequence and
`docs/re/spawn-dissection.md` for the wider NPC/inventory/conversation dissection.
