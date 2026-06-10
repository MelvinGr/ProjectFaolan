# Player spawn generation — COMPLETE (reference)

Goal (chosen 2026-06-10, finished the same day over two sessions): generate the player's own
`SimpleCharFullUpdate` (0xFA015807) spawn entirely from DB character data — name, race, sex, level, vitals,
**full equipment rendering**, and the surrounding scaffold frame — so the server embeds no captured spawn bytes.

> **STATUS: COMPLETE.** The spawn's `ProtoCoreCharacterIIR` (identity/stats/equipment) AND its scaffold frame
> are generated from the character's DB rows. `spawn-player-template.bin` and its successor, the
> `PlayerSpawnTemplate` C# constant, are both gone; Strie's captured bytes survive only as a test oracle
> (`Faolan.Tests/Golden/SpawnStrieTemplate.bin`).
> Verified: the wire grammar parses + regenerates **all captured player-own spawns byte-exact (37/37, incl. captures added after the codec was written)**
> (`Captures/parse_spawn_scaffold.py`); the rows→bytes oracle is locked by
> `PlayerSpawnScaffoldCodecTests`/`PlayerSpawnScaffoldRowsTests`; `tools/client-smoke-test.sh` 16/16 with the
> generated scaffold confirmed on the wire (session-pcap parse); 142 unit tests.

## Architecture (what ships)

The login replay (`RetailLoginReplay.TryRebuildPlayerSpawnFromDb`) rebuilds the player's own spawn in the
world burst in three steps, each with a no-op fallback:

1. **Transform** — `SpawnPacketBuilder.BuildSpawnBody` constructs the header + position (DB) + rotation
   (kept from the capture: rebuilding rotation from unreliable DB data once hung the client).
2. **CharacterIIR** — `PlayerSpawnReader` locates the embedded `ProtoCoreCharacterIIR` (any spawn, any
   layout); name/sex/race/level/vitals are overridden from `Character`/`CharacterMaxStats`, the worn
   equipment from `CharacterEquipment` via `PlayerSpawnEquipment` (`InventoryEntry { pos, ProtoAcgItem }`,
   items built like `SnapshotItemBuilder`); `SpawnPacketBuilder.SetCharacterMessage` splices it back.
3. **Scaffold frame** — `PlayerSpawnScaffoldCodec` (byte-exact parse/generate of the full body) +
   `PlayerSpawnScaffoldRows` regenerate everything around the CharacterIIR from the character's rows:
   - `CharacterAttribute` — the uniform entry table (`Sequence`-ordered; wire order ≠ index order, see demol80)
   - `CharacterSkill` — the feat/ability ID list (`SkillId` ascending + a `Rank` byte)
   - `CharacterScaffoldPair` — the two ending `[id][s32]` pair lists (`ListIndex` 0/1)
   - `CharacterSpawnFrame` — the raw pre-IIR frame blob + the `EntryTableX` scalar

Characters without scaffold rows are lazily seeded ONCE at world entry (`EnsureSpawnScaffoldRows` →
`IDatabaseRepository.SaveCharacterScaffold`) with the **fresh-character defaults** in
`PlayerSpawnScaffoldRows`: Dikketest's extracted scaffold (a just-created character at world entry — 6
weighted attributes, 7 morph entries, the closer, 20 feat ids, fresh pre-frame). Locked by the test
`FreshRows_OverDikketestBody_AreByteIdenticalToTheCapture`.

Buff/timer records (see grammar) are transient session state and are deliberately NOT stored: a buffed
captured spawn extracts to the same rows as its unbuffed form (locked by
`BuffedSpawn_RoundTripsToItsUnbuffedForm`).

## Proven building blocks

- The char message inside the spawn body is a **`ProtoCoreCharacterIIR`** and decodes + re-encodes
  **byte-identically** via `FromProtobuf` / `ToProtobufBytes` (locked by `PlayerSpawnGenerationTests`).
- **The equipment lives inside it**: `ProtoCoreCharacterIIR.InventoryEntries` (field 23, repeated
  `InventoryEntry { pos, item: ProtoAcgItem, gem_slots[] }`); `ProtoAcgItem { key1/2/3: ProtoDecryptionKey
  (= Item.Data1..4), template1/2 = Item.Id, hash = archetype, level }`.
- **`pos` → `CharacterEquipmentSlot` enum** (Neck=1, Head=2, Chest=5, MainWeapon=6, Belt=7, LeftRing=9,
  RightRing=10, Cape=11, Shoulder=12, Boots=14, Hands=15, Legs=16, Wrist=18). 13/17 of necrol80's worn slots
  map; pos 3/8/20/21 are still unmapped (likely face/offhand/vanity — extend the enum when needed).
- The player spawn is the FINICKIEST packet — historically even a rotation change hung the client on the
  loading screen. Every generation step keeps a verbatim fallback, and changes are gated on the smoke test.

## Wire grammar (validated: regenerates all 37 captured player spawns byte-exact)

Workbench: `Captures/parse_spawn_scaffold.py` (`template` / `players` / `all` / `one` / `dumpb` / `pre` /
`prediff` commands). C# port: `PlayerSpawnScaffoldCodec`.

- `[u32 bodyLen=len-4][u32 FA015807][u32 ack=C350][u32 handle][u32 fieldA]`
- transform group `0x2C`: `0A <len> vec3, 12 <len> quat, (25 <fixed32 scale>,) 18 <varint playfield>`
- **pre-IIR frame** (between the transform and the CharacterIIR length prefix), 3 variants:
  - **228 bytes (in-editor preview spawns): fully constant**, zero per-char data.
  - **239 bytes (normal logins)**: constant skeleton + per-char value islands at fixed absolute offsets:
    u16 @135 (Strie 0x322, Dikketest 0x312, Hoxxling 0x42B, ...), u16 @158 (Dikketest 0x08 vs Strickenen
    0x21; also session-variant, e.g. Hoxxling 0x3ED vs 0x36B0), bytes @145/@163/@167/@246, 3 bytes @81
    (usually 0), final marker byte @303 (08, once 0a). Constants include 0x7E2 (=2018) @178, 0x3F1 @186 and
    the `41 00 00 82 08|0a` closer.
    **NOTE: the u32 @133 is NOT a length** (an early misread): Hoxxling's stays 1067 while his post-IIR
    varies by ±280 bytes, assassinl23's is 2018 (the 0x7E2 constant), and several chars have no length-consistent
    u32 at all. It is the u16 value island @135. The legacy splice path
    (`SetSpawnName`/`SetCharacterMessage`) still adjusts it on resize — the client demonstrably tolerates
    that — but the generation path treats the whole frame as stored data (`CharacterSpawnFrame.PreFrame`).
  - **339 bytes (geared necrol80/demol80)**: adds XP-ish u32s @77-91, a few bytes @102-111, and a 26-slot table
    @188-291 of `[00][type 01|06][u16 value]` entries (slot types constant across chars, values per-char).
- `[u32 innerLen]` + the **ProtoCoreCharacterIIR**.
- **post-IIR stream** (contiguous to EOF; nothing follows it):
  - header `[u32 0][u32 4]`
  - `[u32 2][u32 0x3F1 * (nBuffs+1)]` — the second field encodes the count of **60-byte buff/timer records**
    that follow: `[t0][0x7E2][0][0x1207CF][a][b][0][seq from 4][0xC350][handle][5x u32 0]` (only on chars
    with active buffs; otherwise a bare 0x3F1).
  - `[u32 count][u32 X]` + (`[f32 1.0]` iff count>0) — the entry-table count and the per-char scalar X
    (Strie 0xB7, necrol80 0x2B6, editor preview 0x3F1) = `CharacterSpawnFrame.EntryTableX`.
  - **count entries, UNIFORM 14 bytes**: `[u32 idx][u32 0][u16 tag][u32 value]` = `CharacterAttribute`.
    Three payload flavors share the shape: weighted attributes (`tag` = value, `value` = 0x3F800000 weight),
    **morph entries** (`tag` = type<<8|sliderId, `value` = float bits — see the morph finding below) and the
    closing `[u16][u32]` record. Wire order is the stored `Sequence`, not ascending idx (demol80's closer is
    out of order).
  - iff count>0: **ID list of 5-byte groups `[u8 rank][u32 id]`** = `CharacterSkill`; ids ascending,
    terminated by the group with id 0x3F1 (ranks are small values, e.g. necrol80 has 0x0A/0x41-0x4B on ability
    ids). Iff count==0: a bare `[u32 0x3F1]` terminator instead.
  - ending: TWO count-prefixed pair lists `[u32 N][N x (u32 id, s32 value)]` = `CharacterScaffoldPair` (ids
    are decades 10,20,...,110; demol80 has both lists non-empty) + final `[u32 0x3F1]`.

NPC FA015807 bodies use a different post-IIR header (`[0][5]`, `[0][0x129]`, ...) — out of scope (NPCs are
replayed from the burst).

## Morph mapping — CRACKED & WIRED (2026-06-10, third session)

The editor morph sliders ARE in the spawn (the float-valued scaffold entries), and the mapping is now fully
cracked and implemented (`CharacterMorphs`, used by `ApplyMorph` and `EnsureFreshRows`):

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
spawn byte-for-byte). The old verdict in spawn-dissection-plan.md ("the floats are progression skills, NOT
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
  `ApplyMorph` records the editor's refs → `CharacterBodyResource` (send order); world-entry seeding
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

## Remaining (optional)

- **Unmapped equip `pos` values** 3/8/20/21 (extend `CharacterEquipmentSlot` when an item needs them).
- **FullChar (0x737A6DF9)** is the analogous next generation target (test the `ProtoFullCharacterIIR`
  round-trip first, same playbook).
- Extraction of CAPTURED characters into rows currently exists as the test-side mapping
  (`PlayerSpawnScaffoldRows.ExtractTo`); a Faolan.Importer step that materializes e.g. necrol80 as a playable
  geared character would reuse it as-is.

See [[world_entry_from_db]] for the surrounding burst/server-generation architecture and
docs/spawn-dissection-plan.md for the wider (NPC/inventory/conversation) dissection history.
