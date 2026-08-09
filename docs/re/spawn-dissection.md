# World-packet dissection: players & NPCs from the DB

> **STATUS: COMPLETE — kept as the byte-level derivation reference, not open work.** The verified per-message
> index + coverage are [`../protocol/gen/messages.md`](../protocol/gen/messages.md) and
> [`../protocol/gen/coverage.md`](../protocol/gen/coverage.md).

> **NOTE (verified 2026-07): the opcode/IIR hex, byte offsets and field grammar in the body are accurate — that
> is what this reference is for — but many *code/tool/path* references are 2026-06/07 snapshots that later
> refactors (which this frozen doc predates) have moved or removed:** `SpawnPacketBuilder`/`SpawnHandleTable`
> now live in `Faolan.Core.Network/Codecs/Spawn/` (not `Faolan/GameServer/Spawn/`); the `RetailLoginReplay`
> class no longer exists — its `TryRebuildPlayerSpawnFromDb`/`ReplayMapWorldBurst`/`RewriteBurstInstance` logic
> is now in `Faolan/GameServer/World/WorldBroadcaster.cs` + `GameServer/GameServerListener.WorldEntry.cs`, and
> conversation replay in `GameServer/Dialogue/ConversationService.cs`; the create-editor `HandleCommandPacket`
> handlers were folded into `CreateFlowService`; and `ArchiveDataImporter`, `ArchetypeHarvester`/
> `ArchetypeHarvestRegenerator`, `tools/capture/extract_conversations.py`, the embedded `conversations-*.bin`/
> `captured-items.tsv`, the `Faolan/GameServer/Packets/0x737A6DF9_Test/` tree and the whole
> `Faolan.PacketAnalyzer[.Tests]` project are all gone (PacketAnalyzer retired in #82; item-identity harvest is
> now `Faolan.Importer/Extractors/ItemHashHarvester.cs`; conversations are DB-driven via the `ConversationNode`
> table). Trust the body for grammar, not for current symbol/file locations.

> ## ⭐ DISSECTION STATUS 2026-06-11 — the byte-level dissection is COMPLETE for every major world packet family.
> Each captured world packet now parses into a typed model and rebuilds **byte-exact** (the gate for this whole
> plan), verified by round-trip over the capture corpus:
> - **`FA015807` entity spawn** (player + NPC) — header/transform/CharacterIIR dissected; player spawn fully
>   DB-generated; NPC identity lifted to `NpcSpawn` columns; `NpcSpawnBuilder` rebuilds byte-exact. The 227B
>   pre-IIR block is characterized as a sparse `0x3F1` scaffold (not encrypted; decodable like the post-IIR one).
> - **`96C46740` MoveTask** — grammar CRACKED, and since superseded by the client-derived recursive grammar in
>   [`movetask-grammar.md`](movetask-grammar.md): `MoveTaskCodec` round-trips **18,455/18,455 (100%)** byte-exact.
> - **`66AEDD50`/`F096251E`/`33A56FB0` world objects** — message + tail DISSECTED; tail round-trips **1854/1854**;
>   `WorldObjectCodec` rebuilds byte-exact.
> - **`737A6DF9` inventory snapshot** — containers + tails parse→reserialize byte-identical (`InventorySnapshotTests`).
> - **`642CD3D6` Interact** (inbound) — actionType/args/trailing slots+handle decoded (combat, equip, conversation).
>
> The sections below are the per-family dissection detail + history.

> **2026-06-10: the PLAYER-SPAWN half of this plan is COMPLETE and documented in
> `docs/re/player-spawn-generation.md`** — the player's FA015807 is fully generated from DB rows
> (identity/stats/equipment via `ProtoCoreCharacterIIR`, the scaffold frame via `SimpleCharFullUpdateCodec` +
> the appearance model + CharacterSkill/CharacterScaffoldPair rows);
> `spawn-player-template.bin` and the `PlayerSpawnTemplate` constant are both gone. The morph-encoding
> blocker recorded below is SUPERSEDED: the editor sliders are the float-valued scaffold entries. The
> sections below cover the NPC-spawn, static-world and conversation families and dissection history.

## The three hard-won gotchas

These cost the most to find and are the ones that still bite anything touching a world burst:

1. **Inject burst packets via the raw `client.SendAsync` path with a manual CRC**, never `PacketStream.SendAsync`.
   Byte-identical bytes sent the second way stalled the client on the loading screen — PacketStream's synchronous
   CRC work changes the TCP segmentation of the tightly-batched burst, and the client does no reassembly on the
   uncompressed download, so it misframes.
2. **Rotation was kept verbatim** through this whole dissection: rebuilding it from the DB also stalled loading,
   and existing characters carry unreliable rotation data.
3. **The player's entity handle IS the client instance**, and the client never polls its own handle — so no
   per-instance SpawnCheck logic is needed for the player.

## The identity component — CharacterStats

The identity/stat component (sex/side/race/class/morph scale/life/health/…) is a `0x22` length-delimited field
**immediately after the name**, carrying `ProtoCoreCharacterIIR.CharacterStats`. In the Strie template it is
`0x22` @315 with a 67-byte payload (`SpawnPacketBuilder.CharacterStatsTagOffset`), fields
`1=sex 2=side 3=race 4=monster_scale 5=monster_data 6=life 7=health …`, decoded across every player capture
(sex/race vary: Strie 3/1, hoxxling/necrol80/demol80 2/3). The generated type re-encodes byte-identical
(`CharacterStatsRoundTripTests`), so the component is built from a typed object rather than spliced.

The mapping onto DB columns: `race` (field 3) is the `Race` enum; `sex` (field 1) is the `Sex` enum **+ 2**
(Male=2, Female=3); `life`/`health` are **×100** the player-facing `CharacterMaxStats` values.

Splicing it at a *different* length adjusts three prefixes — @0 / @133 / @304 — the same nested-length rebuild
the name field needs (see "Name field" below).

## Tooling

Implemented: `NpcSpawnAppearanceTests` (workbench: decodes handle/position/rotation/name per spawn, plus a
`diff` mode); `Faolan.Core.Network/Codecs/Spawn/SpawnPacketBuilder.cs` (then template-based; the embedded
`spawn-player-template.bin` has since been retired — spawns are generated, see the top banner);
`Faolan.Core.Network/Codecs/Spawn/SpawnHandleTable.cs` (per-session unique/stable handle allocator, base 0x00090000);
`SpawnPacketBuilderTests` byte-exact rebuild + `SpawnHandleTableTests` (the spawn tests now live in
`Faolan.Tests/`). Phase 0 complete.

## Dissection history

**Phase 1 — Player self-spawn from DB** (position only).
`RetailLoginReplay.TryRebuildPlayerSpawnFromDb` detects the player's own `FA015807` in the world burst (the
one whose handle == client instance) and rebuilds its **position** from the DB `Character` via
`SpawnPacketBuilder.BuildSpawnBody`; everything else stays verbatim. For Strie the DB position equals the
capture, so output is byte-identical (no regression); editing the DB position moves the spawn.

GOTCHA 1 - send path: the rebuilt spawn MUST go out the same raw `client.SendAsync` path as the rest of the
burst. Sending it via `PacketStream.SendAsync` instead stalled the client on the loading screen even though the
bytes were byte-identical - most likely PacketStream's synchronous CRC work changed the TCP segmentation of the
tightly-batched burst and the client (no reassembly on the uncompressed download) misframed it. So we splice
the new body in, recompute the CRC in-place (`Crc32.Calculate(rebuilt[8..])`), and send raw. Important for the
whole plan: built packets injected into the burst should use the raw path + manual CRC, not PacketStream.

GOTCHA 2 - rotation: rebuilding the **rotation** from the DB also stalled loading (and existing characters have
unreliable rotation data), so rotation is kept verbatim from the template.

**Phase 2 — Any character (instance generalization).**
The login gate changed from `clientInstance == 0x02165EFF` to `Character.MapId == 0xFAA`, so ANY beach
character uses the world-burst replay. `RetailLoginReplay.RewriteBurstInstance` re-addresses the capture's
instance to the live one in every burst packet: the receiver-conversation varint + inventory nested varints
(`varint(0x02165EFF)=FF BD D9 10`) and the raw entity-handle/body uint32s (`02 16 5E FF`), both 4 bytes for
in-range ids so length-preserving, then recompute CRC and send raw. Verified structurally: 75 varint + 7 raw
substitutions, all 71 receivers + the player handle updated, lengths preserved; for Strie (new==old) it is a
no-op byte-identical (no regression). The SpawnCheck responder is now gated on the new
`Session.UsedWorldBurstReplay` flag (set in the replay) instead of the Strie instance; its status-0x02 handle
(`0x00081906`, the Casilda/intro NPC) is unchanged across characters, and the client never polls its own
handle, so no per-instance logic is needed.

NPC dialogue is now also generalized: `ConversationReplay.TryReplayConversation` substitutes the instance in
BOTH forms (raw uint32 in bodies + the varint in the receiver conversation ids, via `ReplaceAll`) and the
clicked NPC handle, and is gated on `Session.UsedWorldBurstReplay` instead of the Strie instance. No-op
byte-identical for Strie. Confirmed in-client: dialogue opens for a non-Strie character.

COVERAGE LIMIT (capture-replay): `extract_conversations.py` now MERGES all captures (dedup by
(TypeId,Mode,Field3), prefer a 4-byte-varint instance + most packets) -> 12 nodes for the beach NPC type. But
we can only replay branches that appear in the pcaps: e.g. the option `(42008, mode=2, 0)` is in no capture, so
clicking it does nothing (graceful). FULL dialogue-tree advancement (any option, every NPC) cannot come from
packets - it needs the game's conversation CONTENT (client drives options by category/key; server returns the
node from a content DB). That is a separate content-import phase, out of scope for replay.

Still the capture's appearance/name for the player (Phase 3 / name-rebuild).

**Name field.** The entity name is a length-prefixed string `0A <len> <utf8>`
at body offset 308 in the Strie player template. The enclosing lengths were nailed down (the earlier `@296`
candidate was coincidental): name → `@304` (4-byte BE, 469) → `@133` (4-byte BE, 802) → body length `@0` →
packet length. Proven by self-consistency: within `@133`'s `[137,939)`, `167 + (4 + @304-block 469) + 162 =
802`. `SpawnPacketBuilder.SetSpawnName` splices a new name and adds the delta to `@304`/`@133`/body-length;
`RetailLoginReplay.TryRebuildPlayerSpawnFromDb` now applies it (after position) and reassembles the
variable-length packet (header + new body, set packet length, recompute CRC). Round-trip tests cover
same-length / unchanged / grow / shrink (9/9 spawn tests pass). 1-byte name length assumed (names < 128).

**Appearance dissection (2026-06-07, partial).** The attribute/appearance message is `field4` of the post-name
protobuf message (the message that starts with `field1`=name at body offset 308). Inside `field4`:
- `field1` = **Race** (enum-confirmed: Cimmerian=2, Stygian=3 -> Strie=3 Stygian, Faolanx/DemoL80=2 Cimmerian).
- `field2` = likely **Sex** (0=Male for all three test chars; unconfirmed - need a female capture).
- `field3` (1/3), `field4` (92/103) = other appearance params (face/body presets, TBD).
- `field6/7/8/9` = large values (9000/23000/...) — guessed "morphs" at the time; WRONG: this message is the
  `ProtoCoreCharacterIIR.CharacterStats` (field6/7 are life/health on the ×100 scale, e.g. 9000 = 90 HP) and
  the real morphs live in the post-IIR scaffold entries (see the 2026-06-10 banner).
- **Class is NOT in the spawn** (no 0x12-0x2C value anywhere) - it's char-list metadata, not spawn-visual.

MORPH PERSISTENCE — DONE (needs in-client test via the create flow). The create-editor handlers
(`HandleCommandPacket`) now populate `CharacterBodies`: `EnsureBody` lazily creates+tracks the body row;
`ChangeHeadMesh/Size/Voice` write to it (were no-ops); `SetMorphValue` body sliders (arm/chest/bosom/stomach/
arse/thigh/leg, value 0-100) map to the matching `CharacterBody` float fields; resource refs (`*hairmesh/
*texscript/*beardmesh`) and face sliders not yet seen are logged+skipped. Saved with the character at
name-commit. (The "STILL TODO: how do these encode into the spawn" question is answered 2026-06-10: they encode
as the post-IIR scaffold's float-valued entries, NOT into `field4` — see SPAWN MORPH ENCODING below; the
slider→float mapping is the last open piece before appearance renders from `CharacterBodies`.)

SPAWN MORPH ENCODING — ~~BLOCKED~~ **SOLVED for the location (2026-06-10): the morphs are NOT in `field4`.**
The earlier search failed because it looked in the wrong place: the editor sliders are the **float-valued
entries of the post-CharacterIIR scaffold entry table** (generated from `CharacterBody`, payload
`[u8 type][u8 sliderId][f32 bits]`). Dikketest's 7 type-0x01 entries (slider ids 0x00-0x06) match his editor
slider multiset {59×4, 14×2, 30×1} exactly; Hoxxling, who modified ALL sliders, has ~26 type-0x00 entries
(face slider ids 0x02-0x24). Only the exact slider→float mapping remains (59 → −0.03, 14 → +0.007,
30 → +0.015 for Dikketest — not a simple linear map). Full grammar in
docs/re/player-spawn-generation.md.

**Phase 3 — Appearance & orientation fidelity.**
Dissect the morph/appearance region of `FA015807` and source it from `CharacterBodies` (populated from the
create-editor `SetMorphValue` stream — currently empty). Fix `0x2024` ReadyForPlayScreen to write **big-endian**
floats and the two distinct positions (`Packets.cs:516`). Add `0x2052` per-entity data from the model.

**Phase 4 — NPC spawns from DB.**
Reuse `SpawnPacketBuilder` for NPCs sourced from `Npcs` / `NpcBodies` / `NpcEquipment`, keyed by map. Replace
`SpawnTortageBeach`'s verbatim dikketest blobs with built spawns; allocate NPC handles from the table; add to
the `0x2076` manifest. Verify a beach NPC built from a seeded `Npcs` row matches its captured `FA015807`.

DISSECTION DONE (2026-06-10) — workbench `NpcSpawnAppearanceTests` (`one <handle-hex>` = full field
dump, no-arg = table of every NPC in the Strie login burst). **An NPC spawn is the exact same FA015807 grammar
as the player spawn** — only the field VALUES differ; the byte layout is identical:
```
[0:20]    header: bodyLen, subOp=FA015807, ack=0xC350, handle, fieldA
[20:~66]  transform group 0x2C: 0A vec3(pos LE), 12 quat(rot LE), 18 playfield-varint
[66:293]  pre-IIR block — FIXED SIZE 227B, per-NPC float CONTENT (extended transform / bounds), still RAW
@297      ProtoCoreCharacterIIR (u32 length prefix @293; iirOff is a CONSTANT 297 for every beach NPC):
            f1  name
            f4  CharacterStats   sex/side/race/monster_scale/monster_data/life/health/.../head_mesh/level/radius
            f5  NpcStats         f4=template_id (NPC identity), super_family, community, npc_flags, npc_type
            f7  CharacterBcc     idle/action anim ids
            f23 inventory_entry  worn equipment (humanoids only; ABSENT on animals — Crocodile/Panther)
[iirEnd:] post-IIR scaffold — GRAMMAR CRACKED 2026-06-12: header [u32 0][u32 firstRecordLen=len-36][u32
          recordCount]; word0=0 on ALL 42 NPCs (player has 2 there), recordCount=1 on 41/42. Minimal
          freshly-spawned form is a CONSTANT 41B block (23/42 NPCs, single distinct value); larger blocks
          concatenate runtime-state records (movement waypoint vec3 BE float triples + buff/timer pairs) past
          the first. -> SpawnPostIirs.BuildMinimalPostIir() (C#, tested byte-exact vs the Crocodile golden).
```
The whole identity/appearance/stats/equipment of an NPC lives in the CharacterIIR, which decodes cleanly with
the existing `ProtoCoreCharacterIIR` type (the same one the player spawn already round-trips byte-exact). Key
finding: **`NpcStats.template_id` (IIR f5.f4, fixed32) is the stable per-type identity** — Crocodile=14049,
Pillager=8756/14040 (two variants), Kalanthes=2704 (= the archive `npcs` MODEL id, not the npc row id), Casilda
=9411. Instances of the same template share race/sex/monster_data/head_mesh; `monster_scale` (f4) carries the
per-spawn size jitter (e.g. crocodiles 129–136). So an NPC spawn is reconstructable from a per-template row
(name, the CharacterStats/NpcStats/Bcc field set, equipment list) + per-instance (handle, position, rotation,
monster_scale). POST-IIR scaffold REGENERATABLE 2026-06-12: the freshly-spawned form is a constant 41B block
(`SpawnPostIirs.BuildMinimalPostIir`), and `WithMinimalPostIir(body)` strips a body's post-IIR to it (the
candidate fix for the cross-session render rejection — wired behind `FAOLAN_MINIMAL_SCAFFOLD=1`, run alongside
`FAOLAN_BUILT_NPC_TEST=1` to test in-client). STILL RAW: the 227B pre-IIR block — it has NO constant form (only
35/227 bytes stable even across the 10 freshly-spawned NPCs; the rest are per-instance handle/spatial values),
so its minimal/stripped form is in-client-gated on that experiment.
PRE-IIR BLOCK CHARACTERIZED 2026-06-11: it is the (body-relative) [66:293] = 227-byte region. Constant/varying
analysis over the 42 beach NPCs: only ~8% of bytes are constant, ~152/227 are zero (sparsely, different
positions per NPC), and it carries the **`03 f1` (0x3F1) marker @112-113** — the SAME scaffold-entry family as
the dissected POST-IIR scaffold. So it is NOT encrypted/opaque; it is another sparse 0x3F1-keyed scaffold
region (extended transform/bounds + state entries) that yields to the same pair-list decode as
`SimpleCharFullUpdateCodec` — a dedicated effort, not a blocker (NpcSpawnBuilder ships it raw from `Data` today).

PRE-IIR LAYOUT DECODED FROM THE CLIENT (Ghidra, 2026-06-14, Gitea #185). `SimpleCharFullUpdateIIR_t::ReadSubClass`
= `FUN_01121c00` (anchor "...Received a bad stream..."), reproduce with `tools/ghidra/DumpSpawnIir.java` +
`DumpFns.java`. Read primitives: `FUN_0048f900` = read u32 (byte-swapped when the big-endian flag is set),
`FUN_004e0390` = read 1 byte, `FUN_01122b10` = read a **0x3F1-keyed scaffold** (`word = u32; count = word/0x3F1 -
1; if word % 0x3F1 == 0 read `count` records`), `FUN_0145ac80` = read the embedded CharacterIIR (+ tail). The
pre-IIR is therefore **NOT** a uniform sparse scaffold — it is a **FIXED field sequence**: after the version
byte (==5), the reader does **53 u32 + 6 single-byte reads + exactly ONE embedded 0x3F1 scaffold** (≈222 B,
== the ~227-byte region). For a freshly-spawned NPC that scaffold is EMPTY — its count word is the literal
`00 00 03 F1` (value 0x3F1 → count 0), which is exactly the "`03 f1` marker @112-113" earlier mis-read as a
scaffold key. So the lift is: model the fixed field sequence as a struct (most fields zero / per-instance
spatial), emit the empty 0x3F1 scaffold word, and the block regenerates from columns — no raw `Data` blob, no
cross-session stale refs. (Caveat: cross-session humanoid render still has a separate unknown — minimal
post-IIR breaks humanoids, `memory npc_spawn_minimal_postiir_breaks_humanoids` — so prove the regenerated
pre-IIR in-client.)

PREMISE CORRECTION 2026-06-14 (#185): the long-standing claim that cross-session NPC rows fail to render
because their **pre-IIR / post-IIR carry the wrong session's instance refs is WRONG.** Scanning 8 beach NPC
bodies (`body = Data[12+headerLen:]`, pre-IIR `body[66:293]`, post-IIR `body[297+iirLen:]`): the pre-IIR
contains **no entity handle** (not even its own; the only handle-range value is a CONSTANT `0x00050000` @ pre+43),
and the post-IIR (41–83 B) is the minimal scaffold + float movement-waypoint triples (values ending `…3f` =
float exponents), **also no handles**. So both blocks are session-INDEPENDENT (spatial + constants + the NPC's
own waypoints) and fully regeneratable from columns. The real cross-session render blocker is therefore NOT
these blocks — it is elsewhere (the CharacterIIR content, or a client-side gate), and
[[npc_spawn_minimal_postiir_breaks_humanoids]] (minimal post-IIR breaks humanoids) is consistent with
"humanoids need their captured post-IIR waypoints, don't strip them" rather than a ref problem.

IN-CLIENT BISECT 2026-06-14 (#185, driven via Faolan.ClientLauncher + server-side diff logging). Forcing the
23 diverged beach NPC rebuilds onto the wire (`FAOLAN_FORCE_NPC_REBUILD=1`): **no crash, no `pcDynel is NULL`**
(the old world-entry crash is GONE) — the client just **silently doesn't render** them (= the "missing NPCs").
So diverged bodies are silently dropped, not fatal. Diffing each rebuild vs its burst body (skipping len+crc):
two patterns, both at the **transform/pre-IIR boundary** (`bodyStart=35`, firstBodyDiff ~rel-body 3 or 61),
never the identity/IIR:
- **Pattern A** — different total lengths (1089/1076, 1844/1855): the row's IIR/scaffold is a DIFFERENT SIZE
  than the burst (a different capture session's runtime state — more/fewer post-IIR waypoint records).
- **Pattern B** — equal lengths, ~10–15% of body bytes differ starting ~rel-body 61: SAME NPC, the per-instance
  transform floats + pre-IIR/post-IIR scaffold values diverge (the row's exact float pos/state ≠ the burst's;
  position-match rounds to 0.1 so sub-precision differences survive).
Conclusion: it is the same NPC with a different session's per-instance transform+scaffold state, and the client
silently rejects a body whose state doesn't match what it expects. The
`FAOLAN_FORCE_NPC_REBUILD` lever + diff logging were experiments (reverted).

MOVETASK CARRIES POSITIONS (2026-06-14, supports the #185 hypothesis + #190). Each burst FA015807 spawn is
paired with a `96C46740` MoveTaskIIR_c, and that MoveTask body is **full of position floats** — a waypoint /
patrol path (e.g. a len-214 beach MoveTask has waypoints at 677.7, 352.1, 741.3, 645.1, 999.1, …; the first
float ~offset 3 is the start position). So when a forced from-DB rebuild changes the spawn's exact position
(row float ≠ burst float, same rounded 0.1), the spawn is now **inconsistent with its paired MoveTask's
waypoints** (the MoveTask is sent verbatim from the burst). That is the leading explanation for the silent
drop, and is exactly what `FAOLAN_NPC_BURST_XFORM` (splice the burst transform into the rebuild) tests.

CONSTRUCTION PROGRESS (2026-06-10):
- ✓ The machinery generalizes (commit 3897e05, `NpcSpawnConstructionTests`): `BuildSpawnBody` reconstructs an
  NPC body (animal + humanoid) byte-exact from its typed pos/rot, and `SpawnBodyReader` decodes the NPC
  CharacterIIR with a byte-identical re-encode (the splice oracle). Golden fixtures `SpawnNpcCrocodile`/
  `SpawnNpcCasilda`.
- ✓ Identity lifted into `NpcSpawn` columns (commit 9734fc7): the importer (`NpcSpawnExtractor`) decodes the
  CharacterIIR and lifts TemplateId/Race/Sex/Level/MonsterScale/MonsterData/HeadMesh/HasEquipment + the rotation
  quaternion (only position was stored before). Populated in faolan.db: 1645/1704 spawns carry a template_id
  (the rest are world objects with no NPC IIR), 1422 humanoids flagged with equipment. `NpcSpawnExtractorTests`
  asserts the lift against the real Casilda/Crocodile bodies. (Importer needs `--skip-characters` on a populated
  DB — the known non-idempotent `CaptureCharacterImporter`, UNIQUE CharacterBodies.CharacterId.)

- ✓ Full construction primitives (commit 84b14dd): `SetCharacterMessage` generalized to locate the IIR via
  `SpawnBodyReader` (works for NPC + player; adjusts body-length @0 + IIR-length @iirOff-4; the @133 island is
  player-only), and `SpawnPacketBuilder.BuildSpawn(template, handle, pos, rot, iir)` composes transform + IIR.
  Rebuilding Casilda/Crocodile from their own transform + decoded IIR is byte-identical.
- ✓ DB-row → wire-body bridge (commit d10faa6): `NpcSpawnBuilder.TryBuild(NpcSpawn row, handle)` drives the
  transform from the row's columns and the IIR + raw pre/post-IIR scaffold from the row's `Data`; byte-identical
  to the capture when the columns match, re-addressing changes only the handle (`NpcSpawnConstructionTests`).

- ✓ Wired into world entry (commit da1489d): `ReplayMapWorldBurst` matches each burst NPC spawn to its
  `NpcSpawns` row by spawn POSITION (the stable dedup key — handles are session-volatile, only 16/41 matched by
  handle vs 40/42 by position) and rebuilds the body via `NpcSpawnBuilder` addressed to the burst's handle
  (keeps the 96C46740 pairing). The table-built body goes on the wire ONLY when byte-identical to the
  re-addressed burst body — zero render risk (beach: ~17/40 matched spawns are byte-identical; the rest are
  cross-session and kept verbatim). `IDatabaseRepository.GetNpcSpawns(mapId)` added.

- ✓ BURST-FREE CONSTRUCT VALIDATED IN-CLIENT (commit a7e6841, 2026-06-11). A from-scratch entity — built
  FA015807 (`NpcSpawnBuilder`) + built stationary 96C46740 (`MoveTaskNodes.Stationary`) on a fresh server
  handle (0x90001), framed onto a live burst header — is POLLED by the client (0x2020→SpawnCheck) and renders
  standing put beside the player. Isolated step-by-step via the `FAOLAN_BUILT_NPC_TEST=1` harness
  (`RetailLoginReplay.InjectBuiltTestNpc`): a clone of a real spawn renders (mechanism/handle/ordering OK), the
  built spawn renders (builder OK), the built stationary MoveTask keeps it put (MoveTask builder OK). So the
  construct-from-scratch path WORKS against the real client. NOTE: the client only polls entities near the
  player's spawn (a far-off injection was ignored), and the SpawnCheck is reactive (answers any polled handle),
  so no manifest entry is needed for an injected spawn.

**Phase 5 — Inventory & equipment.**
The `0x737A6DF9` giant inventory snapshot is already parsed: `Faolan/GameServer/Packets/0x737A6DF9_Test/`
(`InventorySnapshotInspector` = container/item/stats model; `BigboyTailsFile` = the stats/quest tails;
`Send0x2000_0x737A6DF9.cs` loads a captured "bigboy", parses, mutates, rebuilds). Structure: body =
prefix + protobuf root (containers) + tails. Containers are field6 messages keyed by kind: Equipped=101,
Bag=104, Resource=112, Quest=113; each has f1{f1=kind,f2=owner}, f2=capacity (NOT occupied count), f3=flags,
f4=repeated item entries. Item entry: f1=slot, f2=details {f1=itemId, f2=repeated props (23=record id, 26=stack
qty, 0=flags), f3=template tail with f4/f5=fixed32 template id, f8=fixed32 archetype}. Stats live in the tails.

Findings / constraints:
- Mutating an existing item's **stack count** (prop 26) and **stat values** (tails) is SAFE and works in-client.
- **Adding/removing an item entry STALLS the client on loading.** ⚠️ Read the rest of this phase before acting on
  that: it looked like a packet-shape blocker for weeks and it is not one. The cause is the item **identity** —
  a fabricated `Data1..4` quad is rejected — and an entry built from a real row loads fine (see "STEP (b)
  CONFIRMED IN-CLIENT" below). The count/occupancy theory recorded here at the time was wrong.
- DB side is a STUB: `Items` table is just (Id, Name), `CharacterEquipment` empty, `Items` empty. There is no
  item-template schema (template id / archetype / stats), so DB-driven inventory needs that built first.
- Beach login currently sends the captured inventory verbatim from the world burst (instance-substituted), so
  every character gets Strie's items.

ADD/REMOVE STALL INVESTIGATION (2026-06-07): ruled out the packet-side causes.
- The prefix varint (`00000002 a9288106`) tracks the TAILS, not inventory: two Strie inventories with identical
  entry counts (5) differ in that varint by exactly the 5-byte tails delta. So it's not an item-count desync.
- The bag item's itemId / record-id (prop 23) do NOT appear in the tails - no dangling reference on removal.
- Parse -> re-serialize (Prefix + Root.ToArray() + Tails.ToArray()) is BYTE-IDENTICAL (round-trip test
  `InventorySnapshotTests.ParseThenReserialize_IsByteIdentical` passes). So the rebuild machinery is lossless;
  add/remove produces a structurally valid packet.
CONCLUSION: the stall is semantic / client-side (the client cross-references the inventory item set with other
state, or its own cache), NOT a packet bug. The safe capability is solid: lossless parse/modify/rebuild +
confirmed-OK stack-count and stat edits.

ARCHIVE BREAKTHROUGH (2026-06-07): the old C++ server (`Archive/`, extracted alongside the repo)
explains the add/remove stall and reframes the whole inventory approach. (It concluded "snapshot-injection is a
dead end" — ⚠️ that conclusion was overturned the same week: injection works once the identity is real, see
"STEP (b) CONFIRMED IN-CLIENT" below. What survives is the diagnosis of *why* it was failing.)
- Items are NOT patched into a captured snapshot. The retail server adds an item via a **dedicated packet**
  (`WorldServer/WorldServerOpcodes.h` op `0x1d`); `WorldServer/CommandHandler.cpp::addItem` builds it as a
  48-byte `item_pack` header followed by `id, id, iLevel, unk(0x4f4f4939), data1, data2, data3, data4` and then
  the `data1..4` quad **repeated** (this duplicated quad is exactly the 20-byte tail "identity payload" we saw
  in the snapshot), ending `0x3f1, 0`.
- `data1..data4` come from the **`p_items` table** (`trunk/sql/p_items2.sql`: `p_items(name, id, level,
  data0..data4)`) — they are the item's stable, server-known identity. A fabricated/duplicated item with an
  invented identity is rejected by the client (the stall), which is why every add experiment hung even when the
  packet was byte-valid: the identity quad must be a REAL `p_items` row.
- The inventory is therefore built from `player_items` (the per-character equipped/bag rows; schema in
  `trunk/sql/aocdatabase.sql`) joined to `p_items` (the template identity), NOT reconstructed by editing a
  capture. `player_items` slot ids are equipment slots: neck=0x01, head=0x02, tokens=0x03, chest=0x05,
  main_hand_left=0x06, belt=0x07, off_hand_left=0x08, ring_left=0x09, ring_right=0x0a, ... (`npc_items` mirrors
  it for NPCs).

Reframed plan: (a) import `p_items` (item identity/template) into the C# DB + a `player_items`/`CharacterItems`
slot table; (b) build the `737A6DF9` snapshot from `player_items` ⋈ `p_items` (identity quads from real rows);
(c) implement the op-`0x1d` add path for live pickups. Meanwhile the safe shipped path stays: send the
instance-rewritten captured inventory verbatim, and drive counts/stats onto it.

ARCHIVE IMPORT — DONE (2026-06-07). Step (a) is implemented and verified. `Faolan/Helpers/ArchiveDataImporter.cs`
is an idempotent startup seeder (run at startup after the schema migration) that parses the embedded
archive SQL (`Faolan/GameServer/Data/archive/p_items.sql` + `aocdatabase.sql`) and fills the DB when empty:
- `Item` gained the template identity columns `Level, Data0..Data4` (migration `ImportArchiveItemTemplate`);
  **219 item templates** imported from `p_items` (the Data1..Data4 identity quad the inventory/add packets need).
- **21 spells** from `spelldata` -> `Spells`.
- **4 Tortage-beach NPCs** (incl. Kalanthes 433376) from `npcs`/`npc_details`/`npc_items` ->
  `Npcs`/`NpcBodies`/`NpcEquipment`, equipment FKs resolved against the imported item templates.
Verified in `Faolan.PacketAnalyzer.Tests/ArchiveDataImporterTests.cs` (2 tests: full import + idempotency) and
against the live `faolan.db`. NOT imported (deliberately): reference tables already modelled as C# enums
(races/classes/archetypes/sex), the speculative stat/ability tables (`const_stats`/`const_abilitys`/`p_abilities`
- no consumer yet), `maps` (already seeded, superset), and the test account/character rows.

ENTRY-FROM-DB — PROVEN BYTE-EXACT (2026-06-07). The exact mapping from a `p_items`/`Items` row to a snapshot
container entry is nailed down and tested. Dissecting the captured Strie inventory, the **"Slave Manacles"**
entry (Equipped slot 18) matches the imported `Items` row 3361557 perfectly:
```
entry.f1                      = slot
entry.f2(details).f1          = itemId            (per-instance GUID, not from template)
details.f2 {f1=0,  f2=v}      = flags property
details.f2 {f1=23, f2=v}      = inventory record id (per-instance)
details.f2 {f1=26, f2=v}      = stack count
details.f3(tail).f1/f2/f3     = 3x identity payload {f1..f4 fixed32 = Item.Data1..Data4}   <- from p_items!
tail.f4 = tail.f5             = Item.Id           (template id)                            <- from p_items!
tail.f7                       = 1
tail.f8                       = archetype          (the ONE field NOT in p_items)
```
`SnapshotItems.ListEntry(slot, itemId, recordId, count, Item, archetype)` constructs this; the test
`SnapshotItemsTests` imports `p_items` into a temp DB, builds the entry from row 3361557 + the captured
per-instance state, and asserts it equals the captured entry **byte-for-byte**. So: p_items SQL -> DB -> builder
-> on-wire bytes is confirmed lossless. (The other 3 captured items aren't in the 219-row partial p_items, so
only Slave Manacles could be cross-checked - but it's conclusive.)

ITEM COVERAGE — FULL IDENTITY HARVESTED FROM CAPTURES (2026-06-07). Archetype (`tail.f8`) is per-template, NOT in
`p_items`, and looks like a hash (Slave Manacles = 1129664565 = 0x43555035; no derivation from the quad). It -
and the `Data1..4` quad - are harvested from real captured inventories, so any item a player has held is fully
buildable even without a `p_items` row:
- `ArchetypeHarvester` parses any 0x737A6DF9 packet and extracts every `templateId -> (Data1..4, archetype)`.
- `ArchetypeHarvestRegenerator` (a test, re-runnable) scans all captures (`.bin` raw + `.json` RawBytesBase64),
  merges, and writes the embedded `Faolan/GameServer/Data/archive/captured-items.tsv` (**177 items, 0 conflicts**).
- `ArchiveDataImporter` merges this into `Items`: it sets `Archetype` on matching `p_items` rows and INSERTS
  capture-only items (quad + archetype, no name/level). **Archetype now lives on `Item`** (new column; the old
  `ItemArchetypes`/`archetypes.tsv` are gone). An item is **buildable iff `Item.Archetype != 0`**.
COVERAGE: **~384 items total, 177 buildable** (every item seen in a capture), up from 8. Verified end-to-end in
`SnapshotItemsTests`: building Slave Manacles from the DB Item alone reproduces the captured entry
**byte-for-byte**. Wider coverage now just needs more captures (re-run the regenerator).

STEP (b) CONFIRMED IN-CLIENT (2026-06-07). ✅ A beach character logs in and the DB-built **Royal Signet**
renders perfectly: correct name, slot type ("Ring - Left/Right Finger"), item level, full stats (+30
Constitution / +52 Magic Damage / +15 Hit Rating), vendor price and bind-on-equip - ALL resolved client-side
from the template id + identity quad we built from `p_items`. The client loads normally (no stall). This proves:
(1) DB-built item entries are accepted in-client; (2) the long-standing add/remove stall was ENTIRELY the
fabricated identity - real p_items-backed items work; (3) the whole item (stats/tooltip/slot) is driven by the
template id + `Data1..4` quad, so a fuller `p_items` is all that's needed for richer items. Snapshot-injection is
no longer a dead end when the identity is real. (Cosmetic: the swap targets the first bag ENTRY, which sits at
slot index 1 -> shows in the 2nd visual cell; fine.)

STEP (b) FIRST INCREMENT — DB ITEM INJECTED INTO LIVE INVENTORY (2026-06-07). Rather
than rebuild the whole snapshot from scratch (the tails are still unreconstructed), the beach-login replay now
REPLACES the bag's first entry IN PLACE with a DB-built entry, keeping the original slot/itemId/recordId so the
entry count and the tails stay byte-stable - the lowest-risk way to prove the client accepts a DB-built item.
- `RetailLoginReplay.ReplayStrieLoginWorldBurst` resolves a fully-buildable demo item (Royal Signet 3774542 -
  `TryResolveInjectableItem` via the new `IDatabaseRepository.GetItem`) and, if found, routes the inventory packet
  through `BuildBeachInventoryPacket` -> `ReplaceFirstBagItemFromDb` (builds via `SnapshotItems`, swaps in
  place, re-serializes with manual CRC). Falls back to the verbatim inventory if the item/archetype isn't
  available, so login stays reliable.
- Count forced to 1 (Royal Signet is non-stackable) to avoid a confound.
- Structurally verified by `SnapshotItemsTests.ReplaceFirstBagItem_InPlace_KeepsCountAndSwapsTemplate`
  (re-parse: entry count unchanged, first bag item now reports template 3774542, itemId/record preserved).

STEP (b) — DRIVEN FROM CharacterEquipment (2026-06-07, pending in-client test). The hardcoded demo is replaced
by a real DB-driven path keyed off `CharacterEquipment` (which already has the per-slot Item navs - no new table):
- `LoadEquipment(character)` loads the character's `CharacterEquipment` (created with the character, dressed at creation by `CreateFlowService`) and reads
  default starter gear if empty: **Wrist = Slave Manacles (3361557)**, **FirstPosBag = Royal Signet (3774542)** -
  both fully buildable and slot-appropriate. Persisted to the DB (re-seeds after a faolan.db revert).
- `ResolveDbInventory` picks the slots that hold a fully-buildable item; `ApplyDbInventory` replaces those
  captured snapshot entries IN PLACE: the Equipped container by slot id (the archive player_items slot bytes:
  chest=0x05, main_hand_left=0x06, legs=0x10, wrist=0x12, ...) via `EquippedSlotItem`, and the first Bag entry
  from FirstPosBag. Each via `ReplaceEntryInPlace` (keeps itemId/record id -> count + tails byte-stable).
- Equipped items are slot-validated client-side, so only slot-correct items are seeded; the bag is permissive.
With the default seed the visible result equals the last test (Royal Signet in the bag; the wrist swap is
byte-identical since Slave Manacles is already the captured wrist item) - but now BOTH come from the DB
`CharacterEquipment`, persisted and extensible. To show a *different* equipped item we need that item's slot type
(learn per-item, or from a fuller item DB) so it lands in a slot the capture actually has (5/6/16/18).

EQUIP/UNEQUIP PERSISTENCE (2026-06-07, pending in-client test). The player's equip/unequip now writes back to
`CharacterEquipment`. Decoded the move command from the client logs: `0x642CD3D6` Interact, actionType
`MoveEquippedToFromInventory`, `InteractArgs.TypeId` = **42010 (unequip / EquippedToInventory)** or **42011
(equip / InventoryToEquipped)**; after the protobuf args a trailing struct of big-endian uint32s holds
**FromSlot, ToSlot, item-template-handle** (e.g. equip Royal Signet 3774542 -> slot 9 ring_left). Wiring:
- `Handle0x2000` move cases read the handle from the trailing bytes (`unk2`) and call `PersistEquip` /
  `PersistUnequip` (`EquipmentPersistence.cs`).
- `PersistEquip(targetSlot, handle)` resolves the handle via `Database.GetItem`, sets the mapped
  `CharacterEquipment` slot and clears the bag (FirstPosBag) if it held it. `PersistUnequip(fromSlot)` clears the
  slot and parks the item in the bag. Slot<->nav map (`Get/SetEquipmentSlot`) matches `CharacterEquipmentSlot`
  (Chest=5, MainWeapon=6, LeftRing=9, Legs=16, Wrist=18, ...).

  ⚠️ The two paragraphs above are the 2026-06 MODEL, kept for the decode history; the wire facts still hold but
  the storage does not. Since #750 a worn item is an `ItemInstance` in container 101 whose `Slot` IS the wire
  equip slot, so there is no slot<->column mapping left to get wrong (and the gate that tested it went with it),
  an equip is a MOVE of that row rather than a write of a slot column, and `FirstPosBag` is gone. See
  [inventory.md](../subsystems/inventory.md).
- The starting gear is seeded ONCE at character creation, so persisted player changes
  are never overwritten on relogin.
- Non-DB (captured-only) items can't be resolved to a DB row, so their moves are logged and skipped (harmless).

KNOWN LIMITATION (relogin rendering): persistence is correct in the DB, but reflecting it on next login is bound
by the in-place-replace approach - we can only swap entries the captured snapshot already has (equipped slots
5/6/16/18 + the first bag slot). An item equipped into a slot with no captured entry (e.g. a ring, slot 9) is
saved but won't render until we ADD equipped entries; an emptied slot still shows the captured item until we
support REMOVE.

**Phase 6 — Static world & retire the replay.**
DONE (2026-06-08, partial): the world burst is now **DB-driven per map**. `Faolan.Importer`
(`MapWorldBurstExtractor`) stores each captured map's full burst in the `MapWorldBurst` tables; on login
`ReplayMapWorldBurst` replays the burst for the character's map (re-addressed to the live char), so ANY captured
map renders - not just the beach. The embedded `strie-login-worldburst.bin` is retired (the beach burst is seeded
into the DB from that blob, byte-identical). The burst is still the captured RAW packets, not reconstructed from
`Maps`/`MapObjects` - so the LITERAL static-world dissection (`66AEDD50`/`F096251E`/`33A56FB0` -> DB-built) is
still the remaining work, but logins no longer need an embedded blob and any map with a capture works. KNOWN: a
couple of maps whose captured player instance is out of the 4-byte-varint range don't re-address cleanly yet.

STATIC-WORLD DISSECTION DONE (2026-06-10) — workbench `QuestObjectiveRegionTests` (`one <handle-hex>` =
full decode, no-arg = table). The three world-object sub-opcodes share the FA015807 header + 0x2C transform
group but are SIMPLER than entity spawns - there is NO 228-byte pre-IIR float block; the object message's u32
length prefix immediately follows the transform:
```
[0:20]   header: bodyLen, subOp, ack, handle, fieldA
[20:~65] transform group 0x2C: 0A vec3(pos), 12 quat(rot), 18 playfield-varint   (no pre-IIR block)
@msg     [u32 BE msgLen][object message]: f1=id(varint), f2=name(string), f3=nested (e.g. a remoteformat XML
         for interactive props), f5/f8/f12 = trailers
[msgEnd:] tail — DISSECTED 2026-06-11 (workbench `coverage` mode round-trips 1854/1854 captured tails byte-exact):
          [u32 stateHandle][u32 b][u32 strLen][state string][00 00 00 00][bbox: 6x f32 BE][f32 scale][interaction]
```
Tail fields: `stateHandle` = the per-session object-state handle; `state` = the object STATE string
(`object_closed`/`object_open`/`Open`, EMPTY for decor & zones); `bbox` = collision bounds (decor carries tiny
normal-like floats, chests/gates the `7f7fffee` = ±3.4e38 "infinite" default); `scale` = 1.0; `interaction` =
the per-session collision/interaction protobuf (empty for decor, ~290B for a chest, with the nested coord
triples). So the tail's STABLE part (state string + bbox + scale) is now structured; the per-session part is
exactly `stateHandle` + the `interaction` protobuf coords.
Sub-opcode = object CLASS: `66AEDD50` = decor/props (`004709_..._decor_chain_hangingwoman`, "Broken Bottle",
"Acheronian Chest/Gate"), `F096251E` = interactive containers ("Chest [N7FU]", carries the remoteformat in f3),
`33A56FB0` = zone/city label ("City of Tortage"). Beach has 12 (5+6+1). The identity is the object message
(id/name/nested) + tail; the per-session bits are handle + playfield (the world/zone instance id) + transform +
the tail's `stateHandle`/`interaction`.

CONSTRUCTION DONE (2026-06-10, commits 7fce36c + 842f5fd) — same arc as NPC spawns:
- `WorldObject` table (NOTE: the pre-existing `MapObject` is the unrelated archive table) + migration;
  `WorldObjectExtractor` lifts subOpcode/transform/id/name; importer populates faolan.db (644 objects: 548
  decor, 51 containers, 45 zones, all named). `IDatabaseRepository.GetWorldObjects(mapId)`.
- `WorldObjectCodec.Build` (full body from columns — for the future burst-free build) + byte-exact tests
  across all three classes (`WorldObjectFromColumnsTests`/`WorldObjectExtractorTests`).
- Wired into `ReplayMapWorldBurst`: each burst object is matched to its row by position and built via
  `WorldObjectColumns.TryReplaceIdentity` - keeps the LIVE placement (handle/transform/playfield) and
  splices in only the DB object identity (message + tail), put on the wire when byte-identical. Beach: 4/12
  (the static zone + decor) now come from the table byte-identical, zero risk.
- The 8 interactive chests differ in the tail's `stateHandle` + `interaction` protobuf (the
  now-isolated per-session object state — see the tail dissection above), so they keep the verbatim burst body.
  The tail's stable part (state string + bbox + scale) is dissected.

**96C46740 MoveTask — see [movetask-grammar.md](movetask-grammar.md), which supersedes what stood here.**
This section carried the 2026-06 *empirical* model, tokenized out of the captures: a task-type byte @30 with
seven fixed-length forms and one variable "segment list". It was right about where the discriminator sits and it
round-tripped the 4267 records the corpus held then, but it named the forms by what they looked like
(`stationary`, `orient`, `pace`, `follow?`). The client's own reader was recovered two weeks later and gives the
real shape — a **recursive** composite whose byte @30 is a factory `mode` selecting `Wait`/`Path`/`Rotate`/
`Jump`/**`List`**/`Ballistic`/`Animated`/`Formation`/approach-target/`SetPos`/`GroundAlign`, a list element
being another full sub-task. That grammar round-trips **18,455/18,455** captured records byte-exact through
`MoveTaskCodec`; there is no residual "not field-named yet" tail.

What is spawn-specific, and still true:

- Each `FA015807` spawn is **paired** with a `96C46740` MoveTask registering that entity's movement, so a rebuilt
  spawn whose position differs from the burst's is inconsistent with its paired MoveTask's waypoints — see the
  "MOVETASK CARRIES POSITIONS" note in Phase 4.
- The MoveTask does **NOT** use the `0x2C` transform framing, and its floats are **BIG-ENDIAN** — the opposite of
  the spawn transform's LE. Body head is `[u32 bodyLen][96C46740][ack=0xC350][u32 handle][00 00][curPos vec3 BE]`
  before the mode byte.
- A non-patrolling NPC's registration is a constant 31-byte body, which is what unblocked the burst-free NPC
  path: an NPC needs only its position (already in `NpcSpawns`) to register and render standing still, making the
  multi-segment patrol an enhancement rather than a blocker. Confirmed in-client 2026-06-11 — a built spawn paired
  with a built stationary MoveTask is polled and renders standing put (see "BURST-FREE CONSTRUCT VALIDATED
  IN-CLIENT" in Phase 4). A mover is constructed from {position, waypoint path, speed} — the data `PatrolPath`
  holds.

So the burst-free spawn primitives — built spawn + built MoveTask + built world objects on fresh handles — are
validated end to end.

## Phase 7 — NPC conversations (dialogue)

Clicking an NPC sends an `Interact` (`642CD3D6`, actionType `0x16` = TalkToNpc); the clicked NPC's entity
handle is in the args (field 10 / field 2, `args.SpellTarget.TargetHandle`). Retail replies on the player +
201C conversations with: interact echoes (`642CD3D6`), an entity-state block, a `0x2015` "select target",
then the **conversation window**: `4A3D899A` (conversation, id 12493) + `F164A4A5` (`remoteformat` XML, id
7615). All are keyed to the NPC handle and embed the player name/guild in the XML.

We have **many** captured conversations, including full branching trees - not just the intro. Survey
(`4A3D899A` node / category 50002 + `F164A4A5` remoteformat / category 50001):
- The Kalanthes post-creation intro is **id 12493** ("Stranger" greeting - a shared first-greeting node).
- The **strickenbla combat dump** has a fuller tree for its quest/combat NPC: `12493` (greeting) ->
  `12490 -> 20018 -> 20019 -> 20020` (with remoteformats `12621/12622/12623`) -> `19919, 26909, 12492,
  12494` as the player clicks through options.
- The **Strie-randomActions** and **DemoL80** login captures have yet more (`7731/19985/31636`, `21717/22047/
  35071`, etc.).

Mechanism: each player option-click is a `TalkToNpc` whose args encode the chosen branch (e.g. `105f` ->
`105f1820` -> `108c01`); the server replies with the next node(s). The dialogue **text is client-side**
(localized by id, e.g. `9411`); the server only sends node ids + parameters. So an NPC conversation is a
small **state machine** - `(current node, chosen option) -> next node id(s)` - directly extractable from
these captures. The 12493 "Stranger" node is generic; the NPC-specific tree follows it.

Current state: a **data-driven conversation replay engine** is implemented.
- `tools/capture/extract_conversations.py` extracts each `(TypeId, Mode, Field3) -> response packets` node from a
  capture into `Faolan/GameServer/Data/conversations-<tag>.bin` (embedded). Default source is the
  Strie-login-randomActions capture (same beach as the login replay); 5 nodes for the two beach NPCs.
- `ConversationReplay.cs::TryReplayConversation` (gated to the Strie instance) matches the client's TalkToNpc
  `(TypeId, Mode, Field3)`, substitutes the live NPC handle + instance into the captured response packets
  (4->4 bytes, so length prefixes stay valid; `PacketStream.SendAsync` recomputes the CRC), and sends them.
  The `TalkToNpc` handler tries this first, falling back to the create-world Kalanthes intro.

## Archive reference (the OLD C++ server) — a data & protocol goldmine

`Archive/` (extract it to a scratch dir; paths below are relative to its `trunk/`) is the **previous-generation
Project Faolan server written in C++**. It is NOT portable to the C# server (different language/architecture) but it is the
best **reference and DATA source** we have — it shipped real world content. What it gives us, project-wide:

- **`trunk/sql/aocdatabase.sql`** — the full retail-shaped schema: `player_items` / `npc_items` (equipment slots,
  see Phase 5), `npcs` (NPC spawn data: model, map, position), `playerstats` / stat tables, `classes`, maps, etc.
  This is the schema the C# DB should grow toward for DB-driven spawns/inventory.
- **`trunk/sql/p_items2.sql`** — the **item template table** `p_items(name, id, level, data0..data4)`. The
  `data1..4` quad is the item identity used in the inventory snapshot tails and the op-0x1d add packet. This is
  the missing item DB (the C# `Items` table is just `(Id, Name)`).
- **`trunk/sql/` NPC/world data** — `npcs` has named beach NPCs incl. Kalanthes (id 433376, model 2704, map
  4010) — drives Phase 4 (NPC spawns from DB) without needing new captures.
- **`StatIds.txt`** — the stat id ↔ name mapping (for the inventory tails / `playerstats`).
- **`WorldServer/WorldServerOpcodes.h`** — opcode names (e.g. add-item = `0x1d`), a Rosetta stone for our
  `0x2000` sub-opcodes / world ops.
- **`WorldServer/CommandHandler.cpp`** — reference implementations: `addItem` (item packet layout, see Phase 5),
  GM/command handling, spawn logic. Read it as protocol documentation, not code to port.
- **`classes` table** — confirms the `Class` enum (e.g. Priest of Mitra = 24 = 0x18).

How to use it: when a phase needs DATA (item templates, NPC rows, stat ids, equipment-slot meanings, class/race
enums) or a question about "what does retail send here", check the archive first — it usually has the answer that
the captures alone don't.

NOT-YET-IMPORTED archive tables (kept on the shelf — embedded in `Faolan/GameServer/Data/archive/aocdatabase.sql`,
import on demand by extending `ArchiveDataImporter` if/when a consumer appears):
- `const_stats` (+ `const_stats_new.sql`) — per (level, race, class) base health/mana/stamina/exp and S/I/C/D/W
  stat progression. The real data behind a future stats system. (~70 rows.)
- `const_abilitys` / `p_abilities` — class abilities & ability templates (id + data0..4 quad, same shape as
  `p_items`) per class/level.
- `races` / `classes` / `archetypes` / `race_arche_class` — reference/enum data. Already expressed as C# enums
  (`Race`/`Class`/`ArchType` + the valid-combo list in `Statics.cs`); `classes` also carries the client class id
  (e.g. Barbarian=18). Import only if we want them DB-driven.
- `accounts` / `characters` / `body` / `face` / `player_items` / `playerstats` — test/empty rows, intentionally
  skipped (we have our own accounts/characters; `body`/`face`/`player_items`/`playerstats` ship no data rows).
