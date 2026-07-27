# Player spawn generation — COMPLETE (reference)

> **STATUS: COMPLETE — kept as the byte-level derivation reference, not open work.** The verified per-message
> index is [`../protocol/gen/messages.md`](../protocol/gen/messages.md) (`SimpleCharFullUpdateIIR`); the executable
> spec is the codec (`PlayerSpawnScaffoldCodec`), golden-tested.

Goal (chosen 2026-06-10, finished the same day over two sessions): generate the player's own
`SimpleCharFullUpdate` (0xFA015807) spawn entirely from DB character data — name, race, sex, level, vitals,
**full equipment rendering**, and the surrounding scaffold frame — so the server embeds no captured spawn bytes.

> **STATUS: COMPLETE.** The spawn's `ProtoCoreCharacterIIR` (identity/stats/equipment) AND its scaffold frame
> are generated from the character's DB rows. `spawn-player-template.bin` and its successor, the
> `PlayerSpawnTemplate` C# constant, are both gone; Strie's captured bytes survive only as a test oracle
> (`Faolan.Tests/Golden/SpawnStrieTemplate.bin`).
> Verified: the wire grammar parses + regenerates **all captured player-own spawns byte-exact (37/37, incl. captures added after the codec was written)**
> (`tools/capture/parse_spawn_scaffold.py`); the rows→bytes oracle is locked by
> `PlayerSpawnScaffoldCodecTests`/`PlayerSpawnScaffoldRowsTests`; a local smoke-test script, 16/16 with the
> generated scaffold confirmed on the wire (session-pcap parse); 142 unit tests.

## Architecture (what ships)

The burst-less world entry (`GameServerListener.BuildBurstlessPlayerSpawn`, `.WorldEntry`) builds the
player's own spawn from DB in three steps (no captured-burst replay fallback remains — a missing scaffold
just logs a warning and skips the spawn):

1. **Transform** — `SpawnPacketBuilder.BuildSpawnBody` constructs the header + position (DB) + rotation
   (kept from the capture: rebuilding rotation from unreliable DB data once hung the client).
2. **CharacterIIR** — `PlayerSpawnReader` locates the embedded `ProtoCoreCharacterIIR` (any spawn, any
   layout); name/sex/race/level/vitals are overridden from `Character`/`CharacterMaxStats`, the worn
   equipment from `CharacterEquipment` via `PlayerSpawnEquipment` (`InventoryEntry { pos, ProtoAcgItem }`,
   items built like `SnapshotItemBuilder`); `SpawnPacketBuilder.SetCharacterMessage` splices it back.
3. **Scaffold frame** — `PlayerSpawnScaffoldCodec` (byte-exact parse/generate of the full body) +
   `PlayerSpawnScaffoldRows` regenerate everything around the CharacterIIR from the character's rows:
   - `CharacterAttribute` — the uniform entry table (`Sequence`-ordered; wire order ≠ index order, see DemoL80)
   - `CharacterSkill` — the feat/ability ID list (`SkillId` ascending + a `Rank` byte)
   - `CharacterScaffoldPair` — the two ending `[id][s32]` pair lists (`ListIndex` 0/1)
   - the pre-IIR frame + `EntryTableX` scalar are CONSTRUCTED from `Character.Class`
     (`PlayerSpawnScaffoldRows.FreshPreFrame`; `EntryTableX` is const `0xB7`), NOT stored as rows — they
     carry no per-character data (there is no `CharacterSpawnFrame` table)

Characters without scaffold rows are lazily seeded ONCE at world entry (`EnsureSpawnScaffoldRows` →
`IDatabaseRepository.SaveCharacterScaffold`) with the **fresh-character defaults** in
`PlayerSpawnScaffoldRows`: Dikketest's extracted scaffold (a just-created character at world entry — 6
weighted attributes, 7 morph entries, the closer, 20 feat ids, fresh pre-frame). Locked by the test
`FreshRows_OverDikketestBody_AreByteIdenticalToTheCapture`.

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
`prediff` commands). C# port: `PlayerSpawnScaffoldCodec`.

- `[u32 bodyLen=len-4][u32 FA015807][u32 ack=C350][u32 handle][u32 fieldA]`
- transform group `0x2C`: `0A <len> vec3, 12 <len> quat, (25 <fixed32 scale>,) 18 <varint playfield>`
- **pre-IIR frame** (between the transform and the CharacterIIR length prefix) — **decoded**
  (`PlayerSpawnPreFrame` / `PlayerSpawnPreFrameCodec`). It is not a constant skeleton with value islands: it is
  the SAME `SimpleCharFullUpdateIIR_t::ReadSubClass` scaffold the NPC spawn uses (`NpcSpawnPreIir`, grammar in
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
  is why its frame is exactly `NpcSpawnPreIir.FixedLength` + 5 = 228 bytes. Six captured frames carry two or
  four NAMED specs (`PvE - Lance` 23 feats, `PvP` 27, `PvP - Decap/Protection` 28, `PvP - Decap/Armor` 28,
  `Hybrid` 27, `Berserker: No Decap` 26).

  So the 25 captured frame LENGTHS are ONE shape parameterised by the sublist, and the two "landmark constants"
  the generator used to hardcode are list counts: `0x07E2` = `ForCount(1)` (one spec), `0x03F1` = `ForCount(0)`
  (that spec has no feats). The earlier reading of the 339-byte frame as a "geared variant adding XP-ish u32s
  and a 26-slot table of `[00][type 01|06][u16 value]` entries" was the count word plus 25 feat ids: a feat id
  such as `0x0001adb4` reads as `[00][01][adb4]` if you look for a table.

  Named fields inside the 53 words: **4** = the vanity shown-slots mask (#336 found it independently at frame
  offset 18, which is exactly word 4 — corroboration that this grid is the client's), **17** = the same
  category enum `NpcSpawnPreIir` calls the category, carrying its modal `0x6f` on all 259 captured frames,
  **19** = a constant `0x41700000` (float 15.0) on all 259, **21** = a per-session value (6..44, plus round
  outliers 1005/1586/9000/11185/14000) that nothing predicts. The 6 u8 fields carry the archetype
  (Soldier 1 / Priest 2 / Rogue 3 / Mage 4) and the class byte at flags 1 and 2 — the frame's only genuinely
  per-character content besides the mask and the feat specs.

  **NOTE: the u32 @133 is NOT a length** (an early misread): Hoxxling's stays 1067 while his post-IIR
  varies by ±280 bytes, AssassinL23's is 2018 (the sublist's `ForCount(1)` word), and several chars have no
  length-consistent u32 at all. The legacy splice path
  (the obsolete `SpawnPacketBuilder.SetCharacterMessage`, using `NameOuterLengthOffset` = 133) still
  adjusts it on resize — the client demonstrably tolerates that — but the generation path builds the
  whole frame from `Character.Class` (`PlayerSpawnScaffoldRows.FreshPreFrame` → `PlayerSpawnScaffold.PreFrame`).
- `[u32 innerLen]` + the **ProtoCoreCharacterIIR**.
- **post-IIR stream** (contiguous to EOF; nothing follows it):
  - header: a **plain-counted list** `[u32 n][n x u32]` then the constant `[u32 4]`. Read as the constant pair
    `[u32 0][u32 4]` until #251 — `n` is 0 on all but 2 captured spawns, which carry a single word (60).
  - `[u32 2][u32 0x3F1 * (nBuffs+1)]` — the second field encodes the count of **60-byte buff/timer records**
    that follow: `[t0][0x7E2][0][0x1207CF][a][b][0][seq from 4][0xC350][handle][5x u32 0]` (only on chars
    with active buffs; otherwise a bare 0x3F1).
  - `[u32 count][u32 X]` + (`[f32 1.0]` iff count>0) — the entry-table count and the per-char scalar X
    (Strie 0xB7, NecroL80 0x2B6, editor preview 0x3F1) = `PlayerSpawnScaffold.EntryTableX`.
  - **count entries, UNIFORM 14 bytes**: `[u32 idx][u32 flag][u16 tag][u32 value]` = `CharacterAttribute`.
    `flag` was read as a constant 0; 2 captured spawns carry a 1 on their LAST entry, and there `value` is a
    plain integer (300, 400) rather than float bits — so it discriminates how `value` is read.
    Three payload flavors share the shape: weighted attributes (`tag` = value, `value` = 0x3F800000 weight),
    **morph entries** (`tag` = type<<8|sliderId, `value` = float bits — see the morph finding below) and the
    closing `[u16][u32]` record. Wire order is the stored `Sequence`, not ascending idx (DemoL80's closer is
    out of order).
  - iff count>0: **ID list of 5-byte groups `[u8 rank][u32 id]`** = `CharacterSkill`; ids ascending (ranks are
    small values, e.g. NecroL80 has 0x0A/0x41-0x4B on ability ids). The list is CLOSED by a group whose "id" is
    a **`ListFrame` word**, followed by that many **13-byte records** `[u32 0xC350][u32 id][u32 0][u8 kind]`.
    `0x3F1` is `ForCount(0)`, which is why the close was long written down as a terminator constant; 17 captured
    spawns carry `ForCount(1)`/`ForCount(2)`. The closing group's rank byte is nonzero on 23 spawns and is
    carried, not asserted. Iff count==0: a bare `[u32 0x3F1]` instead.
  - ending: TWO **plain**-counted pair lists `[u32 N][N x (u32 id, s32 value)]` = `CharacterScaffoldPair` (ids
    are decades 10,20,...,110; DemoL80 has both lists non-empty), then a THIRD pair list that is **count-framed**
    (`[u32 ListFrame word][n x (u32 id, s32 value)]`). That word is what used to be called the final `0x3F1`
    marker; 10 captured spawns carry `ForCount(1)`, every observed element being `(975, 0)`.

Three of those fields are decoded structurally but NOT semantically (the tail records, the entry kind flag, the
third pair list's `(975, 0)` element) — carried verbatim; open work tracked in Gitea #466.

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
`[u32 id][u8 rank]` behind a LEADING frame word, where `PlayerSpawnScaffoldCodec` carves `[u8 rank][u32 id]` groups
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
  sliders produce NO entry (strickenbla, zero morphs: 6 weighted attributes + closer at idx 7).
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
  weighted-entry source: wire order = editor send order, weight = the registry weight (1.0), and
  **category 2 (face paint, the always-sent `*texscript_183875`) is never emitted**. faolanx resolves
  **7/7 bit-exact** against this client's registry; the other five create chars resolve 4-6 of 6, the
  residuals (e.g. tint_char_blank consistently −4) being live-server↔client data-version drift — the same
  drift visible in the body sliders, where retail's wire (arse=0x104/thigh=0x105/leg=0x106, proven across
  37 spawns) disagrees with this client's registry (arse=0x106/thigh=0x107/leg=0x37). Generation follows
  the **client's own registry** (it's the table this client ships) with the retail-proven morph table kept
  for the sliders.
- Implementation: `Faolan.Importer --rdb` imports the registry → `MorphTarget` (Id = wire tag);
  the create-flow `RecordBodyResource` records the editor's refs → `CharacterBodyResource` (send order); world-entry seeding
  resolves refs → `PlayerSpawnScaffoldRows.BuildWeightedEntries` (falls back to Dikketest's set when
  nothing resolves). Locked by `WeightedAppearanceTests` (faolanx 7/7).
- **The closing record is session state, not character data**: its tag is always a multiple of **0x3F1**
  (the grammar's magic: Dikketest 22×0x3F1, Hoxxling 18/19/22/32×0x3F1 across his logins — same char,
  different sessions) and its value one of 100/300/400. Like the buff records it varies per login, so the
  stored Dikketest closer (22×0x3F1 / 300) is kept for generated spawns.

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
- **Container generation WIRED & PROVEN IN-CLIENT**: `FullPlayerCharacterBuilder` builds the 4 containers
  entirely from the character's DB items (typed; deterministic instance+slot-derived ids); the world-entry
  snapshot now replaces the captured player's containers wholesale — entry count included (smoke test
  16/16 with a 2-entry Equipped container vs the capture's 4; the old in-place swap survives only as the
  fallback when the captured root doesn't decode).

- **Live tail values PATCHED from the character** (same session): world entry now writes the LIVE level
  (tails header), current HP/mana/stamina (`TailsPostAccessBlock`, ×100 slots 54/64/68) and the XP bar
  into the snapshot tails before sending — the captured player's values no
  longer ride along. Best-effort with the captured values as fallback; locked by the bigboy round-trip in
  `FullPlayerCharacterBuilderTests` (the stat table MUST be recognized on the reference fixture).
  Smoke-verified in-client 16/16: generated containers + patched tails live in the same session, no
  fallback warnings in the server log.

- **Bag rows WIRED (same day)**: `CharacterInventory` now has its writer — unequip parks the item as a bag
  row at the client-given cell (multiple unequips coexist; the old single-`FirstPosBag` parking lost items),
  equip consumes the source row; world entry renders the rows in their containers by kind (`BuildContainers`
  inventoryRows; the seeded FirstPosBag yields its cell to a persisted row; entries pos-ascending). Locked
  by `InventorySlot_UpsertAndClear_RoundTripAcrossContexts` + `BuildContainers_RendersPersistedBagRows`;
  smoke 16/16 with a live bag item rendered from rows.

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
