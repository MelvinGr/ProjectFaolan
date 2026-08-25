# Player spawn generation — COMPLETE (reference)

> **STATUS: COMPLETE — kept as the byte-level derivation reference, not open work.** The verified per-message
> index is [`../protocol/gen/messages.md`](../protocol/gen/messages.md) (`SimpleCharFullUpdateIIR`); the executable
> spec is the codec (`SimpleCharFullUpdateCodec`), golden-tested.

Goal (chosen 2026-06-10, finished the same day over two sessions): generate the player's own
`SimpleCharFullUpdate` (0xFA015807) spawn entirely from DB character data — name, race, sex, level, vitals,
**full equipment rendering**, and the surrounding scaffold frame — so the server embeds no captured spawn bytes.

> **STATUS: COMPLETE.** The spawn's `ProtoCoreCharacterIIR` (identity/stats/equipment) AND its scaffold frame
> are generated from the character's DB rows. `spawn-player-template.bin` and its successor, the
> `PlayerSpawnTemplate` C# constant, are both gone; Strie's captured bytes survive only as a test oracle
> (the `golden/SpawnStrieTemplate.bin` entry of `Faolan.Tests/Assets/fixtures.bin`).
> Verified: the wire grammar parses + regenerates **all captured player-own spawns byte-exact (37/37, incl. captures added after the codec was written)**
> (`tools/capture/parse_spawn_scaffold.py`); the rows→bytes oracle is locked by
> `SimpleCharFullUpdateCodecTests`/`PlayerSpawnScaffoldRowsTests`; a local smoke-test script, 16/16 with the
> generated scaffold confirmed on the wire (session-pcap parse); 142 unit tests.

## Architecture (what ships)

The burst-less world entry (`GameServerListener.BuildBurstlessPlayerSpawn`, `.WorldEntry`) builds the
player's own spawn from DB in three steps (no captured-burst replay fallback remains — a missing scaffold
just logs a warning and skips the spawn):

1. **Transform** — `SpawnPacketBuilder.BuildSpawnBody` constructs the header + position (DB) + rotation
   (kept from the capture: rebuilding rotation from unreliable DB data once hung the client).
2. **CharacterIIR** — `SpawnBodyReader` locates the embedded `ProtoCoreCharacterIIR` (any spawn, any
   layout); name/sex/race/level/vitals are overridden from `Character`/`CharacterMaxStats`, the worn
   equipment from the character's `Equipped` `ItemInstance` rows via `PlayerSpawnEquipment` (`InventoryEntry
   { pos, ProtoAcgItem }`,
   items built like `SnapshotItems`); `SpawnPacketBuilder.SetCharacterMessage` splices it back.
3. **Scaffold frame** — `SimpleCharFullUpdateCodec` (byte-exact parse/generate of the full body) +
   `PlayerSpawnScaffoldRows` regenerate everything around the CharacterIIR from the character:
   - the APPEARANCE table is **DERIVED, not stored** (`PlayerSpawnScaffoldRows.BuildAppearanceEntries`):
     weighted elements from `CharacterBodyResource` through the `MorphTarget` registry (face paint first —
     retail's slot 1), morph elements from `CharacterBody`. `Word2` slot ordinals are `1..n` in that order.
   - `CharacterSkill` — the SKILL list (`SkillId` + `Rank`, the client's `[u32 id][u8 rank]` pairing); the
     archetype ability that opens it (archetype × 100) and the class-progression entry are DERIVED per send
   - `CharacterScaffoldPair` — the two ending `[id][s32]` pair lists (`ListIndex` 0/1)
   - the pre-IIR frame is CONSTRUCTED from `Character.Class`
     (`PlayerSpawnScaffoldRows.FreshPreFrame`), NOT stored as rows — it
     carries no per-character data (there is no `CharacterSpawnFrame` table)

The entry table used to be persisted as `CharacterAttributes` rows and re-synced from the appearance model on
every edit path; the table is gone. The registry it resolves through reaches the builder as a parameter
(`IDatabaseRepository.GetMorphTargetRegistry`, read once per process — 1,072 rows of static seed content).

Characters without scaffold rows are seeded ONCE at creation
(`CreateFlowService.SeedSpawnScaffold` → `IDatabaseRepository.SaveCharacterScaffold`) with the
**fresh-character defaults** in `PlayerSpawnScaffoldRows`: the progression a character starts with (feat/skill
ids, an empty spec, the fresh pre-frame). The APPEARANCE is deliberately NOT among them — it is derived from
`CharacterBody` + `CharacterBodyResource` on every send, so there is nothing to seed and no default face to
inherit. Locked by the test `FreshRows_OverDikketestBody_AreByteIdenticalToTheCapture`, which reproduces
Dikketest's captured spawn from his own appearance model (his refs through the registry, his sliders).

**Retail's entry indices are per-character SLOT ids, not positions** — they survive appearance edits, so a
re-barbered character carries gaps and appends its re-chosen resources after its morphs (`NecroL80` runs
2..15, `DemoL80` runs 4..13 then 24,23). Nothing derives them and nothing needs to: every character we build
spawns with `1..n` and renders. So those two captures round-trip to the same `(tag, value)` multiset with a
canonical index run rather than byte-identically — pinned by
`EditedAppearance_RoundTripsExceptItsRetailSlots`, while `SpawnStrieTemplate` / `SpawnDikketestLogin` /
`SpawnHeraldBuffed` stay byte-exact through the derivation.

Buff/timer records (see grammar) are transient session state and are deliberately NOT stored: a buffed
captured spawn extracts to the same rows as its unbuffed form (locked by
`BuffedSpawn_RoundTripsToItsUnbuffedConstructedForm`).

## Proven building blocks

- The char message inside the spawn body is a **`ProtoCoreCharacterIIR`** and decodes + re-encodes
  **byte-identically** via `FromProtobuf` / `ToProtobufBytes` (locked by `PlayerSpawnGenerationTests`).
- **The equipment lives inside it**: `ProtoCoreCharacterIIR.InventoryEntries` (field 23, repeated
  `InventoryEntry { pos, item: ProtoAcgItem, gem_slots[] }`); `ProtoAcgItem { key1/2/3: ProtoDecryptionKey
  (= Item.Data1..4), template1/2 = Item.Id, hash = archetype, level }`.
- **`pos` → `CharacterEquipmentSlot` enum** — all 17 retail slots mapped (2026-06-10): Neck=1, Head=2,
  **Tokens=3**, Chest=5, MainWeapon=6 (main_hand_LEFT), Belt=7, **OffHandWeapon=8** (Ceremonial Dagger),
  LeftRing=9, RightRing=10, Cape=11, Shoulder=12, Boots=14, Hands=15, Legs=16, Wrist=18,
  **RangedWeapon=20** (Executioner Crossbow), **Ammo=21** (Bolt Quiver); gaps at 4/13/19, bag=17. The melee
  pair is 6/8 (left navs), the ranged pair 20/21 (right navs) - fixing a spawn-builder bug where MainWeapon
  read MainHandRight while persistence wrote MainHandLeft.
- The player spawn is the FINICKIEST packet — historically even a rotation change hung the client on the
  loading screen. The verbatim captured-burst fallback has since been removed (burst-less construction is the
sole path); changes are gated on the smoke test.

## Wire grammar (validated: regenerates all 37 captured player spawns byte-exact)

Workbench: `tools/capture/parse_spawn_scaffold.py` (`template` / `players` / `all` / `one` / `dumpb` / `pre` /
`prediff` commands). C# port: `SimpleCharFullUpdateCodec`.

- `[u32 bodyLen=len-4][u32 FA015807][u32 ack=C350][u32 handle][u32 fieldA]`
- transform group `0x2C`: `0A <len> vec3, 12 <len> quat, (25 <fixed32 scale>,) 18 <varint playfield>`
- **pre-IIR frame** (between the transform and the CharacterIIR length prefix) — **decoded**
  (`SpawnPreIir` / `SpawnPreIirCodec`). It is not a constant skeleton with value islands: it is
  the SAME `SimpleCharFullUpdateIIR_t::ReadSubClass` scaffold the NPC spawn uses (`SpawnPreIir`, grammar in
  `docs/re/fa015807-spawn-reader.md`), plus the head of the embedded CoreCharacter:

  ```
  [u8 version 0x05] [6 x u8 + 53 x u32, in the reader's fixed order, around one count-framed SUBLIST]
  [u8 CoreChar version 0x41] [u32 CoreChar IirHeader]
  ```

  The last two fields are why the frame appeared to end in a magic `41 00 00 82 08|0a` "closer": the player
  parse cuts at the CharacterIIR's u32 length prefix, which sits 5 bytes into the CoreCharacter's 9-byte header
  `[u8 0x41][u32 iirHeader][u32 msgLen]`. The `iirHeader` is the same word `NpcSpawnBuilder` writes from
  `NpcStatCore.IirHeader` (0x8208 or 0x820A; not per-character).

  **The SUBLIST is the character's saved FEAT SPECS**, one entry each:
  `[u32 index][u8 index][u16 len + Latin1 name][count-framed list of u32 feat ids]`. Every normal login carries
  exactly ONE entry with an empty name — which is why the frame's bytes 119..121 read as three reserved zeros
  and the whole entry read as a lead-in to a bare feat list — and the create-editor preview carries none, which
  is why its frame is exactly `SpawnPreIir.FixedLength` + 5 = 228 bytes. Six captured frames carry two or
  four NAMED specs (`PvE - Lance` 23 feats, `PvP` 27, `PvP - Decap/Protection` 28, `PvP - Decap/Armor` 28,
  `Hybrid` 27, `Berserker: No Decap` 26).

  So the 25 captured frame LENGTHS are ONE shape parameterised by the sublist, and the two "landmark constants"
  the generator used to hardcode are list counts: `0x07E2` = `ForCount(1)` (one spec), `0x03F1` = `ForCount(0)`
  (that spec has no feats). The earlier reading of the 339-byte frame as a "geared variant adding XP-ish u32s
  and a 26-slot table of `[00][type 01|06][u16 value]` entries" was the count word plus 25 feat ids: a feat id
  such as `0x0001adb4` reads as `[00][01][adb4]` if you look for a table.

  Named fields inside the 53 words: **4** = the vanity shown-slots mask (#336 found it independently at frame
  offset 18, which is exactly word 4 — corroboration that this grid is the client's), **17** = the same
  category enum `SpawnPreIir` calls the category, carrying its modal `0x6f` on all 259 captured frames,
  **19** = a constant `0x41700000` (float 15.0) on all 259, **21** = a per-session value (6..44, plus round
  outliers 1005/1586/9000/11185/14000) that nothing predicts. The 6 u8 fields carry the archetype
  (Soldier 1 / Priest 2 / Rogue 3 / Mage 4) and the class byte at flags 1 and 2 — the frame's only genuinely
  per-character content besides the mask and the feat specs.

  **NOTE: the u32 @133 is NOT a length** (an early misread): Hoxxling's stays 1067 while his post-IIR
  varies by ±280 bytes, AssassinL23's is 2018 (the sublist's `ForCount(1)` word), and several chars have no
  length-consistent u32 at all. The legacy splice path
  (the obsolete `SpawnPacketBuilder.SetCharacterMessage`, using `NameOuterLengthOffset` = 133) still
  adjusts it on resize — the client demonstrably tolerates that — but the generation path builds the
  whole frame from `Character.Class` (`PlayerSpawnScaffoldRows.FreshPreFrame` → `SimpleCharFullUpdateIIR.Frame`,
  which is a typed `SpawnPreIir` — the scaffold holds no frame bytes).
- `[u32 innerLen]` + the **ProtoCoreCharacterIIR**.
- **post-IIR stream** (contiguous to EOF; nothing follows it):
  - header: a **plain-counted list** `[u32 n][n x u32]` then the constant `[u32 4]`. Read as the constant pair
    `[u32 0][u32 4]` until #251 — `n` is 0 on all but 2 captured spawns, which carry a single word (60).
  - `[u32 2][u32 ListFrame]` — the buff list, GROUPED BY SPELL, in the client's own carve
    (`FUN_0145b8a0`/`FUN_0145c0b0`/`FUN_007ed9b0`, #658): per group `[u32 spellId][u32 ListFrame]` then that
    many 52-byte records (a discarded u32 + twelve fields: timer pair, caster level, the
    `[EntityClass.Character][handle]` reference to whoever the buff is on — decoded flat in #475, re-carved
    grouped). The inner frame was the flat model's "Kind = ForCount(1) constant": every captured group holds
    one record. An empty list frames as a bare `0x3F1`, which is `ForCount(0)`, not a constant.
  - ⭐⭐ **the ENTRY SLOT — ONE grammar for both families, and the old player carve was 8 bytes OFF the
    client's** (#466, decided by `FUN_010087b0`/`FUN_0079cfd0` — see "Where the CLIENT reads the scaffold
    lists" below). The client reads:

    ```
    [u32 count]  count × [u32 key][f32 value][u32 word2][u16 nameLen][name]   the APPEARANCE table
    [u32 ListFrame][n × [u32 id][u8 rank]]                                    the SKILLS
    [u32 ListFrame][m × [u32 50000][u32 inst][u32 slot][u8 kind]]             the PETS
    ```

    Each appearance element goes to `MorphValues_c::SetMorphValue(key, value, word2, name)`; the name is empty
    on every captured element (the key carries the identity), which makes the element 14 bytes and is why a
    shifted 14-byte grouping (`[u32 idx][u32 flag][u16 tag][u32 value]` behind an "X scalar + 1.0 weight word"
    header) round-tripped byte-exact for years. Under the client's carve, every invented field dissolves:
    "EntryTableX" + the asserted 1.0f were the FIRST element (usually the face paint `0xB7`, retail's slot 1;
    NecroL80's `0x2B6` is his beard, his face-paint slot vacated); each old entry's `(tag, value)` was one
    element's `(key, value)` with `idx` belonging to the NEXT element as its `word2` slot ordinal; the
    "closer" record was the SKILL list's frame word + the first skill's id; the "flag-1 entry" was that frame
    word overflowing 16 bits on 65+-skill characters (all seven mystery tags satisfy
    `(0x10000|tag) % 0x3F1 == 0`, framing 71..120 skills); and the ID list's "terminator rank" was the last
    skill's rank. Weighted elements carry `value` = the registry weight (1.0), morph elements the slider float.
    `word2` is a per-character slot id retail keeps across appearance edits, so DemoL80's re-chosen hair/beard
    sit at 24/23 after its morphs.
  - **the SKILLS** = `CharacterSkill`, `[u32 id][u8 rank]` per element (the client stores `map[id] = rank`).
    Every captured player's list opens with its ARCHETYPE ability (100/200/300/400 = archetype × 100, rank 0 on
    all 296) and ascends from there. The last element's rank is nonzero on 28 captured spawns.
  - **the PETS**: the second `ListFrame`'d list — ⭐ see below.
    `0x3F1` is `ForCount(0)`, which is why both closes were long written down as terminator constants; 17
    captured spawns carry pets `ForCount(1)`/`ForCount(2)`.
  - ending: TWO **plain**-counted pair lists `[u32 N][N x (u32 id, s32 value)]` = `CharacterScaffoldPair` (ids
    are decades 10,20,...,110; DemoL80 has both lists non-empty), then a THIRD pair list that is **count-framed**
    (`[u32 ListFrame word][n x (u32 id, s32 value)]`). That word is what used to be called the final `0x3F1`
    marker.

### ⭐ The 13-byte tail records are the character's PETS (#466)

Named from the client end to end, which is what the ticket asked for:

| step | function |
|---|---|
| the list | `FUN_0145ba10` — `ListFrame`'d, count capped at `0x7531` |
| the element | `FUN_014578e0` — three u32s and a byte into a 16-byte struct |
| the APPLY | `FUN_01458d40` (the spawn apply) walks that vector with **stride 0x10** and hands each record to `FUN_0081b960` |
| the object | `FUN_0081b960` allocates **`BasePet_c`** (RTTI `.?AVBasePet_c@@`, 0x28 bytes) |
| the register | `FUN_0081b670` — its own assert cites **`CoreCharacter\Pets\petholder.cpp`** with parameter `i_pcNewPet`, and it logs `"PetHolder_c::RegisterPet: Pet registered (instance %i)!"` on channel `GameCode/Pets` |

```
[u32 50000][u32 PetInstance][u32 SlotSelector][u8 Kind]
     +0x18        +0x1C           +0x20          +0x0C     <- on BasePet_c
```

`50000` is `EntityClass.Character` and the consumer special-cases exactly that value to resolve the referent, so
the leading pair is the ordinary `(class, instance)` entity reference. `PetInstance` is what the log line prints
and what `RegisterPet` de-dupes on (it compares `+0x18` and `+0x1C` against the pet already held).

⚠️ **`SlotSelector` was asserted to be ZERO and is not a constant.** `RegisterPet` tests `pet->+0x20 == 1` and
puts that pet in the holder's SINGLE slot at `+0x4c`, releasing whatever was there; anything else joins the
general list. It is 0 on every captured spawn because no captured character had a primary pet out — so a player
who did would have failed our parse. Carried now.

`Kind` (0x01 / 0x04 in the captures) is `BasePet_c`'s own field at `+0x0C` and is still unnamed.

### ⭐ The third pair list is a STAT list, and that is what the asymmetric framing was saying (#466)

The 8-byte `(id, value)` shape is shared by **two unrelated families**. Lists 1 and 2 are the faction-reputation
block (#315) — ids in the decades, values a signed standing. List 3's ids are wire **stat** ids, and every one
resolves in the client's own name table (`docs/re/aoc-client-stat-names.tsv`):

| id | client name | carriers | values | family |
|---|---|---:|---|---|
| `20` | `BCCState` | 8 | 1 | NPC spawns |
| `975` | `DamageAbsorb` | 39 | 0 | both |
| `1351` | `NaturalEnergyRegeneration` | 9 | 5625 / 7368 | **player only** |
| `1000028` | `Energy` | 9 | 3222 / 9000 / 14000 | **player only** |
| `1000029` | `MaxEnergy` | 9 | 9000 / 14000 | **player only** |

⭐ **The three energy stats land on the same 9 spawns**, with `Energy <= MaxEnergy` on every one — a pool, its
denominator and its regen rate pushed together. That is what makes them a deliberate block rather than noise.

⚠️ **So `MaxEnergy` IS on the wire**, and the standing note that it *"appears ZERO times in 120 captures"* with
its bytes sitting in *"the retail server's leaked scaffold region … not a value it wrote"* was reading this list.
It is count-framed, parsed by our own codec and round-tripped byte-exact.
[V3: SpawnPostIirGrammarTests.TheThirdPairList_IsAStatList_NotAThirdFactionList]

#466 recorded this list as *"10 spawns carry `ForCount(1)`, every observed element being `(975, 0)`"* — that is
the player-scaffold subset; over the whole `SimpleCharFullUpdateIIR` corpus it is **56 carriers and five ids**.

Two of #466's three fields remain decoded structurally but NOT semantically — the tail records and the entry kind
flag — and are carried verbatim; open work tracked in Gitea #466.

### The client's readers (V1)

The chain, recovered by decompiling the vtable slot-8 readers (`tools/ghidra/DecompAddrs.java`):

| Function | Is |
|---|---|
| `FUN_01121c00` | `SimpleCharFullUpdateIIR_t::ReadSubClass` (`simplecharfullupdateiir.cpp`), version byte 5 |
| `FUN_01122b10` | the buff/timer list — `0x3F1`-framed, per element `[u32]` + a ~56-byte struct |
| `FUN_0145ac80` | `CoreCharacterFullUpdateIIR::ReadSubClass` (`CoreCharacter\IIR\CoreCharacterFullUpdateIIR.cpp`), version byte `'A'` |
| `FUN_00cbfb80` | the length-prefixed embedded protobuf |
| `FUN_010087b0` | a PLAIN-counted list, per element `[u32][u32][u32]` + a length-prefixed **string** |
| `FUN_0079cfd0` | a `0x3F1`-framed list, per element `[u32][u8]` (5 bytes) |
| `FUN_0145ba10` | a `0x3F1`-framed list (bound 30001), per element `FUN_014578e0` = `[u32][u32][u32][u8]` = **13 bytes** |
| `FUN_004d5900` | a `0x3F1`-framed list of 8-byte pairs; the client **discards the first u32 and keeps the second** |

This independently confirms three of the six corrections above: the head list really is a plain-counted
`std::vector<u32>` (built at `+0x13c/+0x140/+0x144`), the 13-byte tail record is a real framed-list element down to
its trailing byte, and the closing pair list really is count-framed where the two before it are plain-counted.

⚠️ It also shows our field BOUNDARIES are not the client's. `FUN_0079cfd0` reads its 5-byte elements as
`[u32 id][u8 rank]` behind a LEADING frame word, where `SimpleCharFullUpdateCodec` carves `[u8 rank][u32 id]` groups
and treats the frame word as a trailing terminator. Both reproduce the same bytes — the codec round-trips all 259
captured bodies byte-exact — so this is a naming/semantics discrepancy, not a parsing bug, but the client is the
authority and the two should be reconciled. Open on #466.

**`generic_stats` is POSITIONAL against a client-side order table.** The reader asserts
`g_Int32StatsOrder.size() == m_CoreCharacterMessage.generic_stats_size()`, and `FUN_01458d40` consumes the two
together — `g_Int32StatsOrder[i]` is the STAT ID for the value at `generic_stats[i]`. So the protobuf carries
values with no ids, and the ids live in a global the client builds at runtime (`DAT_02d25da4`, a `std::vector<u32>`;
`FUN_01e14b00` is only its destructor, so the population is elsewhere and a static dump yields nothing).

⚠️ This says nothing about the wire pair lists. The assert sits *between* the second pair loop and `FUN_004d5900`
and refers to the embedded protobuf message, not to the pairs just read — those remain unnamed. (An earlier version
of this section claimed the pair lists were the stats; that was a misreading of what the assert guards.)

⚠️ Five of the six corrections above are **the same mistake**: a count word whose list is empty in most captures
reads as a closing constant. `ListFrame` documents the trap for `0x7E2`; `0x3F1` is the `ForCount(0)` face of it and
is just as misleading. All 259 captured player-scaffold bodies now parse AND regenerate byte-exact
(`SpawnFeatListTests`); before these fixes 203 did.

NPC FA015807 bodies use a different post-IIR header (`[0][5]`, `[0][0x129]`, ...) — out of scope (NPCs are
replayed from the burst).

## Morph mapping — CRACKED & WIRED (2026-06-10, third session)

The editor morph sliders ARE in the spawn (the float-valued scaffold entries), and the mapping is now fully
cracked and implemented (`CharacterMorphs.TryApply`, driven by the create-flow `SetMorphValue` handler and `EnsureFreshRows`):

- **The editor's `SetMorphValue` numeric value is a ZIGZAG-encoded signed slider value** ("arse"=31 means
  −16, "arm"=200 means +100). `CharacterBody` stores the decoded signed value.
- **The spawn morph float is simply `signedValue / 1000`** (single-precision). All 35 captured slider values
  across Hoxxling (28, ALL sliders modified) / Dikketest (7) / Strickenen (7) reproduce **bit-exact**; the
  earlier "not a simple linear map" read was the un-decoded zigzag values.
- **Face sliders**: the name's slot prefix ("33_NoseCrookedRight") IS the wire sliderId of the type-0x00
  entry (20 sliders, slots 2..36, one CharacterBody column each).
- **Body sliders**: type-0x01 with fixed ids — arm=0, chest=1, bosom=2, stomach=3, arse=4, thigh=5, leg=6;
  **ears is the odd one out: type 0x00, sliderId 0xFF**. (arm/chest 0↔1 settled by send order; the editor
  always moves them together anyway.)
- **Wire entry order = editor send order**: face by ascending slot, then body alphabetically. Unmoved
  sliders produce NO entry (strickenbla, zero morphs: 7 weighted elements and nothing else).
Locked by `CharacterMorphsTests` (Hoxxling's raw editor stream → his 28 wire entries bit-exact) and the
re-pointed `FreshRows_OverDikketestBody_AreByteIdenticalToTheCapture` (Dikketest's raw stream → his captured
spawn byte-for-byte). The old verdict in spawn-dissection.md ("the floats are progression skills, NOT
morphs") was wrong.

## Weighted attributes — CRACKED & WIRED (2026-06-10, fourth session)

The weighted (1.0) entries at the top of the entry table are the character's **chosen appearance resources**
(hair/beard mesh, hair/eye/skin color, tattoos, scars) — NOT race/class presets, NOT progression:

- The client RDB carries a **customization registry: type 1000076 id 0** — 1,072 records
  `[nameLen][name][u32 category][u32 defaultBits][f32 weight][u32 id]` covering ALL appearance targets:
  the face sliders (category 0, "02_BrowLow"), body sliders (1), and the choosable resources
  (3 hairmesh / 4 beardmesh / 5 piercingmesh / 6 hair color / 7 eye color / 8 skin color / 9+10
  tattoos+scars / 2 face paint).
- **Wire tag = registry id − 1** — for the weighted entries AND the morph sliders (one unified id space:
  "02_BrowLow" id 3 → wire 2 = the slot prefix; "arm" id 0x101 → wire 0x100).
- The editor's `SetMorphValue` **resource refs** ("*texscript_1108153" = the chosen options) ARE the
  weighted-entry source: wire order = editor send order with ONE exception — **category 2 (face paint, the
  always-sent `*texscript_183875`, wire key `0xB7`) moves to the FRONT** (retail's slot 1; the old carve read
  it as the "EntryTableX scalar" and recorded it as "never emitted", #466) — weight = the registry weight
  (1.0). faolanx resolves **7/7 bit-exact** against this client's registry; the other five create chars resolve 4-6 of 6, the
  residuals (e.g. tint_char_blank consistently −4) being live-server↔client data-version drift — the same
  drift visible in the body sliders, where retail's wire (arse=0x104/thigh=0x105/leg=0x106, proven across
  37 spawns) disagrees with this client's registry (arse=0x106/thigh=0x107/leg=0x37). Generation follows
  the **client's own registry** (it's the table this client ships) with the retail-proven morph table kept
  for the sliders.
- Implementation: `Faolan.Importer --only rdb-morphs` imports the registry → `MorphTarget` (Id = wire tag);
  the create-flow `RecordBodyResource` records the editor's refs → `CharacterBodyResource` (send order); the
  SPAWN resolves refs → `PlayerSpawnScaffoldRows.BuildWeightedEntries` on every send. A character wears what
  it chose, nothing, or the build FAILS: a missing registry and a ref the registry does not know both throw,
  and choosing nothing (or only category 2) yields an EMPTY entry list. There is no substitute face — it used
  to fall back to Dikketest's 6 captured attributes, whose hair `*hairmesh_208094` belongs to exactly one
  per-race/sex list (race 3 / sex 3), so every other character that hit it wore a hairstyle authored for a
  different race's head plus two face markings nobody chose (Gitea #288).
  `CreateFlowService.ReportUnresolvableAppearance` still names an unimported ref once, at creation, where it
  is still actionable. Locked by `WeightedAppearanceTests` (faolanx 7/7) and, against the shipped catalog in
  both directions, by `PlayerSpawnScaffoldRowsTests`.
- **The "closing record" was not a record** (#466): its "tag, always a multiple of 0x3F1" was the SKILL
  list's `ListFrame` word (varying per login because the skill count does), and its "value, one of
  100/300/400" the first skill's id — the archetype ability. Both are derived by the writer now
  (`ListFrame.ForCount(Skills.Count)` / `PlayerSpawnScaffoldRows.ArchetypeSkillId`).

## IIR looks fields — WIRED (2026-06-10, fifth session)

The CharacterIIR's looks are three `CharacterStats` fields, each **half the editor value** (the same 2×
convention as race/class): `head_mesh` = ChangeHeadMesh/2 (dikketest 2285694 → 1142847), `voice_set` =
ChangeVoice/2 (10 → 5), `monster_scale` = ChangeSize/2 (194 → 97) — verified on all five create captures.
The spawn rebuild (`TryRebuildPlayerSpawnFromDb`) now overrides them from `CharacterBody`
(HeadMesh/Voice/Size, raw editor values in the DB); unset values keep the captured field. Locked by
`PlayerCharacterMessage_LooksFields_AreHalfTheEditorValues` against the dikketest golden. Hair/beard mesh,
colors, tattoos and scars have NO IIR fields — they render from the scaffold's weighted entries +
morph entries (both DB-generated above), so the visual appearance pipeline for fresh characters is complete:
IIR (head/voice/scale) + weighted entries (chosen resources) + morph entries (sliders).

## FullChar (0x737A6DF9) generation (2026-06-10, sixth session)

The same playbook applied to the FullChar/inventory snapshot:

- **Round-trip oracle PROVEN**: the snapshot's protobuf root decodes as the recovered `ProtoFullCharacterIIR`
  and re-encodes **byte-identically on 37/39 captured snapshots** (`FullCharIirRoundTripTests`; 2 skipped =
  truncated analyzer captures).
- **Frame grammar**: body after `[subop][c350][instance]` = `[u32 X][u8 Y]` + the **ProtoFullCharacterIIR
  root** (fields: 1 packed character_tag, 4 expansion_flags ≈0xC0001 (expansion accounts only),
  5 subscription_flags, 4× field-6 containers in kind order 101/104/112/113, 7, 8) + the tails
  (`FullCharSnapshotSubClass`, formerly `BigboyTailsFile`/`SnapshotTails`). NOTE: the inspector's `Prefix` currently lumps `[u32 X][u8 Y]` + the leading root
  fields 1/4/5 (its root finder starts at the first container) — harmless, they ride verbatim.
  `X` is a progression counter (0 fresh; NecroL80 15→43, DemoL80 70; quest-ish), `Y` session-varying
  (fresh chars: 0x44 base accounts / 0x49 expansion accounts). Container constants (all captures):
  max_size 1028/24/48/48 (24 = starter bag, grows with upgrades), disabled_slots_count 0/0/12/0, owner =
  instance. Entry ids: itemId = epoch **0x20D82B63**<<32 | server-counter; record id (stat 23) another
  counter; stat 0 = flags (1 / 0x4000001), stat 26 = stack.
- **Container generation WIRED & PROVEN IN-CLIENT**: `FullCharacterRoots` builds the 4 containers
  entirely from the character's DB items (typed; deterministic instance+slot-derived ids); the world-entry
  snapshot now replaces the captured player's containers wholesale — entry count included (smoke test
  16/16 with a 2-entry Equipped container vs the capture's 4; the old in-place swap survives only as the
  fallback when the captured root doesn't decode).

- **Live tail values PATCHED from the character** (same session): world entry now writes the LIVE level
  (tails header), current HP/mana/stamina (`TailsPostAccessBlock`, ×100 slots 54/64/68) and the XP bar
  into the snapshot tails before sending — the captured player's values no
  longer ride along. Best-effort with the captured values as fallback; locked by the bigboy round-trip in
  `FullCharacterRootsTests` (the stat table MUST be recognized on the reference fixture).
  Smoke-verified in-client 16/16: generated containers + patched tails live in the same session, no
  fallback warnings in the server log.

- **Bag rows WIRED (same day)**: carried items got their writer — unequip parks the item at the client-given
  cell (multiple unequips coexist; the old single-`FirstPosBag` parking lost items), equip consumes the source
  cell; world entry renders them in their containers by kind, pos-ascending. Locked by
  `InventorySlot_UpsertAndClear_RoundTripAcrossContexts` + `BuildContainers_RendersBagItems`; smoke 16/16 with
  a live bag item rendered from rows. (The table behind this was `CharacterInventory`, replaced by
  `ItemInstance` in #750 along with `FirstPosBag`, which was a second source for bag cell 1 — see
  [inventory.md](../subsystems/inventory.md).)

### UPDATE 2026-06-12 — beach FullChar now CONSTRUCTED FROM SCRATCH

The post-load DB backpack snapshot and a from-scratch **beach** snapshot landed (see
memory `snapshot_tails_construction_scope`):

- **The "prefix" is decoded and built from scratch** (`GameServerListener.BuildSnapshotPrefix`). This
  paragraph's original byte model — `[u32 X][varint root-length][trailing 0x81 0x06]` with the length as a
  protobuf varint at offset 4 (= const `4483` + rootLength) — was a **misread that was later Ghidra-corrected**
  (`n3InfoItemRemote::Construct`, see `GameServerListener.Outbound.cs`): the prefix is actually the 5 bytes
  `[u8 flag=0][u32 BE blobLen]` where `blobLen = rootLength` (a plain big-endian blob length, NOT a varint,
  NOT `4483`-based). A later intermediate model appended the next 3 bytes `28 81 06` as a constant "field5" —
  they are the root's own `subscription_flags`, and reading `root = blobLen - 3` broke every level-80 snapshot
  (#381). Regenerating the root (a different length) requires
  rewriting that length or the client misframes (`ReadSubClass Wrong IIR_t version`); `SetSnapshotBlobLength`
  (burst path, superseding the byte-misaligned `AdjustSnapshotPrefixRootLength`) / `BuildSnapshotPrefix`
  (from-scratch) handle it, locked by `SnapshotBlobLengthTests` (BE32 length) and `BeachSnapshotConstructionTests` (from-scratch prefix).
- **The tails are built from scratch for beach** (`FullCharSnapshotSubClass`; the captured `BeachTailsTemplate.bin` has since been retired to a test oracle at `Faolan.Tests/Assets/` — the game-static regions now bake in via `AccessWindowCatalog`, #186/#258):
  level/vitals generated, instance re-addressed, the rest a content template. The `UnknownBefore` progression
  block was DECODED — a sequence of `[u32 field-block][length-prefixed <remoteformat> XML]` records; for beach
  it's byte-identical across characters EXCEPT the instance (proven), so the only per-character tails data is
  {level, vitals, instance}. Default on (`FAOLAN_SCRATCH_BEACH_SNAPSHOT=0` to disable). Other maps still carry
  the burst prefix/tails + delta-adjust.
- Bag **moves/drops/splits persist** now (`Faolan/GameServer/Inventory/EquipmentService.cs`, locked by `EquipmentPersistenceTests`).

See [[world_entry_from_db]] for the surrounding burst/server-generation architecture and
docs/re/spawn-dissection.md for the wider (NPC/inventory/conversation) dissection history.

## Where the CLIENT reads the scaffold lists (#466)

#466 proposes attacking the remaining undecoded field through *"the `ReadSubClass` for the spawn scaffold, via
`DumpVtableReaders.java`"*. ⛔ **That points at the wrong function.**
`SimpleCharFullUpdateIIR_t::ReadSubClass` (`FUN_01121c00`, self-named by its own
`"...Received a bad stream upon entering!"` assert and `simplecharfullupdateiir.cpp`) contains **zero loops** —
it is a flat sequence of fixed-field reads. A counted, variable-length entry table cannot be read there.

⭐ The scaffold lists are read by **`FUN_0145ac80`**, which asserts with
`CoreCharacter\IIR\CoreCharacterFullUpdateIIR.cpp` and carries **three** `do` loops. Found by anchoring on the
`.cpp` name rather than on a vtable slot — the asserts name their own translation unit, which is the cheaper
route into this subsystem.

The list readers under it, in READ ORDER (the whole post-protobuf sequence is
`FUN_0145b8a0` → `FUN_010087b0` → `FUN_0079cfd0` → `FUN_0145ba10` → two plain pair loops → `FUN_004d5900`):

| function | shape | what it is |
|---|---|---|
| `FUN_0145b8a0` | `ListFrame`-counted, allocates `0x18` per element | the richer per-element list |
| `FUN_0145c0b0` | a SECOND `ListFrame` count inside it, `FUN_007ed9b0` per element | what `FUN_0145b8a0`'s elements contain |
| `FUN_007ed9b0` | allocates a `BuffInfo_c` (`FUN_00c9cce0(200, 4, 0)` + ctor `FUN_00849770`) and fills it field by field | ⭐ **a BUFF** |
| `FUN_010087b0` | plain count; per element 3 × u32 + a `[u16 len]` string (`FUN_0048f840` → the string reader `FUN_00cbf840`) | ⭐⭐ **the APPEARANCE table** — each element handed to `FUN_01008070` |
| `FUN_01008070` | `(container, u32 key, f32 value, u32 word2, 0)` + the name string | **`MorphValues_c::SetMorphValue`** — its own log strings on channel `GameCode/MorphValues`; upserts a 76-byte entry keyed on `key` (vector reserved by `FUN_00ff5b60`, stride `0x4C`) and ZEROES rival entries whose category is in the exclusive set at `DAT_01f268b4` (hair/colour-style one-of choices). The category comes from the NAME (`FUN_01008b90`); an empty name — every captured element — leaves the key as the identity |
| `FUN_0079cfd0` | `ListFrame`-counted, per element `[u32 id][u8 rank]`, stored `map[id] = rank` | ⭐⭐ **the SKILLS** |
| `FUN_0145ba10` | reads a word, divides by `0x3F1`, calls `FUN_014578e0` per element | the PETS list |
| `FUN_014578e0` | `u32, u32, u32, u8` — **13 bytes** | the pet record (`[0xC350][id][slot][kind]`) |

### ⭐⭐ The appearance reader is what closed #466: the old player carve was 8 bytes OFF

`FUN_010087b0`'s element — `[u32 key][f32 value][u32 word2][u16 nameLen][name]`, 14 bytes when the name is
empty (it is, on every captured element of BOTH families) — walks the same bytes as the old
`[u32 count][u32 X][f32 1.0][count × [u32 idx][u32 flag][u16 tag][u32 value]]` player carve, shifted by 8. Both
consume the region byte-exactly, which is why the round-trip gates could never see it; what decided it is the
client's own read order. Every invented field of the old carve dissolves into this element (the "EntryTableX"
scalar = the first element's key, the "closer" = the skills frame word + first skill id, the "flag-1 entry" =
that frame word overflowing u16 on 65+-skill characters, the "terminator rank" = the last skill's rank — the
full unwind is on `AppearanceEntry` in `SpawnPostIir.cs`, and the grammar section above carries the corrected
reading). Three corroborations from directions the carve argument does not touch:

* all seven "flag-1 tags" satisfy `(0x10000 | tag) % 0x3F1 == 0` — exact `ListFrame` words framing 71..120
  skills;
* the skill list opens with the archetype ability (`archetype × 100`) on all 296 captured player slots — the
  value the old carve knew as "the closer's value = archetype × 100" (#269);
* `MorphTargets` id 183 (`0xB7`) IS `*texscript_183875`, the "always-sent, never-emitted" face paint — i.e.
  the constant "X" was the face-paint element retail puts in slot 1.

It also unifies the families: an NPC's slot is the same grammar with EMPTY skill and pet lists (the two
`ForCount(0)` words long carried as "EntryTableX + EntryTableMarker" constants), so `SpawnEntrySlot` is one
type and the codec's old try-both-families parse is gone.

⭐ **So `FUN_0145b8a0` is a nested list whose leaf is a `BuffInfo_c`.** That matters beyond this page:
`FUN_007ed9b0` is the one open route [`docs/subsystems/npc-spell-applyspell.md`](../subsystems/npc-spell-applyspell.md)
names for the still-unsourced `MagicBuff` descriptor fields (Gitea #658) — *"the client persists/transports a buff
in a layout whose fourth field IS `bool2`, and that is a place a value can be lined up against something
observable"*. This says WHERE that layout comes from on the wire: the character full-update stream, i.e. the same
family of bytes the corpus already holds 1,471 parsed `ScaffoldBuff` records of.

⭐ It IS our buff list — pinned by the full read order (`FUN_0145b8a0` is the first list after the protobuf,
exactly where our grammar has the buffs), and `SpawnPostIir.BuffRecords` now carries the client's grouped carve.

### ⭐⭐ `BuffInfo_c` is the SAME object the MagicBuff apply builds

`FUN_007ed9b0` reads **twelve consecutive u32** and stores them at

```
+0x14  +0x18  +0x1c  +0x20  +0x24  +0x28  +0x2c  +0xa8  +0xac  +0xb8  +0xbc  +0xc0
```

(the 6th and 7th are read into locals first and stored `+0x2c` before `+0x28`, which is a scheduling artefact —
the READ order is still sequential). The object is 200 bytes (`FUN_00c9cce0(200, 4, 0)`), so `+0xC0` is its last
field.

⭐ **Four of those offsets are exactly the ones the apply path also writes** when a `MagicBuffIIR` lands
(`docs/subsystems/npc-spell-applyspell.md` — `ActivateBuff`→`AddBuffInfo` for the apply itself, `UpdateBuff` =
`FUN_007ee710` for the counter refresh). So the scaffold's buff record and the apply payload are **two
encodings of one client object**, and the offsets bridge them:

| offset | what the apply path puts there | position in `FUN_007ed9b0`'s read |
|---|---|---:|
| `+0x24` | wire `descriptor[3]` — the **caster's level** (`AddBuffInfo` arg 4); the counter path refreshes it from `CastingLevel@42` | 5th |
| `+0xA8` | `CounterType@4` (`UpdateBuff`) | 8th |
| `+0xAC` | `Counter@0` — gated `> 0` in `UpdateBuff` ("no counter, nothing to update") | 9th |
| `+0xC0` | `CounterTimeLeft` (`UpdateBuff`); the GUI reads it as remaining time for head-flags-bit-20 spells | 12th |

That is a second, independent population for #658's unsourced apply fields: the corpus holds 1,471 parsed buff
records off the character full-update stream, and they carry the same object's fields under a different framing.

⭐ **The 60-vs-52-byte disagreement is RESOLVED, and the earlier guess was wrong.** The old flat `ScaffoldBuff`
was 60 bytes; the client's element is 52. The extra 8 bytes are NOT the `TargetClass`/`TargetHandle` pair (the
candidate the first pass named) — they are the GROUP HEAD: `FUN_0145b8a0` reads `[u32 spellId]` per outer
element and `FUN_0145c0b0` the inner `ListFrame` word, i.e. the flat carve's `SpellId` + "`Kind` constant". The
model now matches the client (`ScaffoldBuffGroup { SpellId, Records }`), the discarded u32 opens each record
(our `SourceHandle` — the client reads it and throws it away), and a stream whose group holds TWO records — the
case the flat carve would have misparsed — now reads. The apply path's store blocks also anchor the timer pair:
`+0x18` receives duration x stat `0x1D0 DurationModifier` x a constant (the client COMPUTES the scaled total
the scaffold carries precomputed) and `+0x1c` the unscaled duration. (An earlier read of `UpdateBuff`'s block
said `+0x24` receives "the raw arg-3 pointer to the descriptor" — superseded: `UpdateBuff`'s arg 3 is
`CastingLevel@42`, a level, and the apply's own `AddBuffInfo` stores wire `descriptor[3]`, also a level. Both
paths fill `+0x24` with the same KIND of value the scaffold carries, so scaffold `+0x24` and the apply's level
slots line up directly — which is the convergence that confirmed the level-pair assignment.)

⚠️ Both framing readers divide by `0x3F1` to recover the count, which is the same `ListFrame` arithmetic
CLAUDE.md records for the whole image (77 functions divide, 59 multiply, none compares) — so the framing is
confirmed from the client side here, not just inferred from captures.

⭐ (An earlier pass noted "the parse does not branch on the entry flag" and sent the hunt toward a consumer of a
stored flag. Superseded: there is no flag — see the appearance-reader section above.)
