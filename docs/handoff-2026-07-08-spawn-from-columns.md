# Handoff — 2026-07-08 — NPC spawn from columns (#244) + post-IIR tail (#243)

Session range: `53452950..4bbe7ff8`. Two threads: **#244** (construct NPC spawn bodies from typed DB
columns instead of splicing the captured `Data` blob) — landed + wired + verified — and **#243**
(decode the humanoid runtime post-IIR so the last ~155 spawns drop `Data` too) — half-built. Plus a
**critical login regression** the #244 wiring introduced, now fixed.

---

## 0. READ FIRST — the login regression (fixed, but understand it)

**Symptom:** logging into an existing character (or finishing create → enter world) left the client
showing the **character-creator UI overlaid on the in-game world** — char rendered in the right place,
but the Select Gender/Class panels stayed up. Happened for BOTH existing-login and create-world-entry.

**Root cause (commit `b729e493`, fixed in `dcedd4d5`):** the #244 live-spawn wiring **baked the
playfield-instance token into each NPC `SpawnBody` at map-load time** (`NpcSpawnBuilder.TryBuildLive`
with `ZoneFor(mapId,instanceId).PlayfieldToken`) and **removed the per-send `SpawnPlayfieldRewriter`
re-stamp** from `WorldBroadcaster.SendWorldSpawn`. But the zone's load-time token is a DEFAULT
(`0xa00001`); the real token isn't assigned until a player actually enters the world (`0x507927`).
The retail client **silently drops any spawn whose playfield token ≠ the playfield it loaded** — it
never renders it and never sends its `0x2020` poll. So every NPC was dropped, the client never polled,
world entry never completed, and the loading/creator UI stayed stuck.

**Fix:** restored the per-send token rewrite in `SendWorldSpawn`
(`SpawnPlayfieldRewriter.TryRewrite(body, _sim.World.ZoneForEntity(subject).PlayfieldToken)`). The
token baked at build is now just a placeholder; the live token is stamped when the entering player
exists. **Never re-bake it at load time** — see memory `spawn_playfield_token_per_send`.

**How it was found / how to re-verify (loopback):** `dumpcap -i lo -f "tcp portrange 7000-7090" -w
/tmp/x.pcapng` (background), start server, drive client, `Captures/scripts/tshark_dump.sh /tmp/x.pcapng`, then
compare the **player's own** fa015807 spawn token to the **NPC** spawn tokens — they MUST be equal;
confirm the client then sends `0x2020` and the server answers `0x2008`. The verification script is in
the session transcript (extract token at body offset 60 = `0x18` tag + 4-byte LEB128). Post-fix wire:
all 76 spawns carry `0x507927`, 104 client polls, 105 `0x2008` responses.

Also in this fix: reverted an earlier wrong turn where I gated the auth trio on `isNewCharacter`. The
dumps prove `0x200b`/`0x200c`/`0x2001` are **byte-identical in login AND create** (same
`snd=0xb171ce13 rcv=0x6c67c147`), so they are NOT the create-vs-login differentiator — send them for
both (the differentiator is purely the following `0x2000` burst content).

---

## 1. #244 — NPC spawn from columns (DONE + WIRED + VERIFIED)

**Goal:** build the whole FA015807 NPC spawn body from typed DB columns, no captured `Data` splice.

**Data model — the three tiers** (`docs/npc-three-tier-architecture.md`):
- `Npc` — the type/template (DB). Carries `NpcStatCore` (1:1 side table) = the type-constant CharacterIIR
  stat core, lifted off `Data` into ~60 nullable proto-mirror columns + `IirHeader` (the CoreChar header
  word `X`, RE'd as a type-stable field-presence mask).
- `NpcSpawn` — the placement (DB, static): position/rotation + per-placement overrides (`Life`/`Mana`/
  pools/`Level`/`HeadMesh`/`GuildId`/`WireHealth`/sight/hearing/smelling…).
- `WorldEntity` — the live runtime (in-memory): canonical fresh state (full HP, idle anim, live handle/
  pos/token), pets/summons/loot. Never stored.

**Builders (all in `Faolan.Core.Network/Codecs/Spawn/`):**
- `NpcStatCoreCodec` — explicit (no-reflection) proto↔typed: `Lift(msg)→NpcStatCore`, `ToStats`/
  `ToNpcStats`/`ToBcc(core)`, `ReadIirHeaderWord(body)`. Round-trips all 3097 sub-messages byte-exact.
- `NpcSpawnBuilder.BuildMessageFromColumns(row)` — the whole `ProtoCoreCharacterIIR` from Name +
  `ApplyStatCore` + equipment rows.
- `NpcSpawnBuilder.BuildFromScratch(handle, iirHeader, pos, rot, token, message, humanoid, morphs)` —
  the whole body: header consts + transform (`2C 0A <vec3> 12 <quat> 18 <token>`) + `NpcSpawnPreIir`
  minimal pre-IIR + CoreChar header `[41][X][msgLen]` + message + post-IIR.
- `NpcSpawnBuilder.TryBuildLive(row, handle, playfieldToken, out body)` — the LIVE path: from-columns
  for clean cases, else `Data`-splice fallback. **Wired into `WorldBroadcaster` map-load** (line ~754);
  `GetNpcSpawns` now `.Include(Npc.StatCore)`.

**Coverage (whole-DB gate, `NpcSpawnFromScratchTests.EverySpawn_BuildsFromScratch_OrIsRuntimePostIir`):**
**824/988 spawns build byte-exact from columns**, 9 canonically cleaner (drop a captured runtime target),
155 are the humanoid runtime-post-IIR tail (keep `Data`) — 0 unexpected.

**Verify:** `dotnet test Faolan.Tests --filter NpcSpawnFromScratchTests` and `--filter NpcStatCoreCodecTests`.

---

## 2. #243 — the humanoid runtime post-IIR tail (ACTIVE — half-built)

The 155 spawns still on `Data` are the ones whose post-IIR isn't the clean appearance shape. RE
(`docs/npc-three-tier-architecture.md`, commit `cad8a151`) of 847 equipped humanoids:

| shape | count | status |
|---|---|---|
| clean-appearance | 600 | ✅ `BuildPostIir(morphs)` |
| **no-morph humanoid** — 41-byte minimal form, `recordCount=2`/`count=0` (vs animal's `1`) | ~148 | ✅ **`BuildMinimalPostIir(2)` verified byte-exact (140/140), commit `4bbe7ff8`** |
| runtime-bearing — large first record (movement/bounds float triples) with the **morph table embedded AFTER it** | ~99 | ⏳ needs the lenient extractor |
| entry-level runtime (a morph entry carries runtime in field2/field3) | 2 | ⏳ later |

### The exact next step — lenient morph extractor for the 99

For `TryBuildLive` to drop `Data` on the tail, it must tell the 148 ("genuinely no morphs" →
`BuildMinimalPostIir(2)`) from the 99 ("customised, morph table buried after a runtime record"). The
clean way: **lift the 99's morphs** so they get morph rows (→ `BuildPostIir`), leaving the 148 as the
no-morph remainder. A fresh spawn drops the runtime record anyway, so extracting just the morph table
gives the canonical fresh form.

1. **Dissect the 99's structure in C#** (Python can't — locating the post-IIR needs the C# IIR reader
   `PlayerSpawnReader.TryReadCharacterMessage`). Write a throwaway `[Fact]` (model it on the deleted
   `HumanoidRuntimePostIirTests` in the transcript): for each equipped humanoid where
   `TryDecodeAppearance` fails and header `recordCount==1`, header is `[u32 0][u32 firstRecordLen][u32 1]`,
   then a large `record1` (runtime), then the morph table `[u32 count][count × (u32 id, f32 val, u32 0,
   u8 0, u8 strLen, str)]`, then the shared 20-byte `Terminator`. Pin the morph-table offset relative to
   `firstRecordLen` (candidate: `12 + firstRecordLen`; if not, scan for the `[count][valid-entries]` that
   ends exactly at the terminator). The clean grammar/constants are in
   `NpcSpawnAppearanceCodec` (`Record1`, `EntryFixedLength`, `Terminator`).
2. **Add `NpcSpawnAppearanceCodec.TryDecodeAppearanceLenient`** that skips the runtime `record1` and
   decodes the trailing morph table. Round-trip test: `BuildPostIir(lenient(post))` reproduces the
   morph-table portion.
3. **Importer:** use the lenient decode in `NpcSpawnExtractor.TryBuildEntry` (currently
   `NpcSpawnAppearanceCodec.TryDecodeAppearance(...)` → `morphs`) so the 99 get morph rows. Re-run
   `dotnet run --project Faolan.Importer -- --only npcs` (kill server first, then commit `faolan.db`).
4. **Wire `BuildFromScratch`:** post-IIR = `morphs.Count>0 ? BuildPostIir(morphs) :
   BuildMinimalPostIir(humanoid ? 2u : 1u)`; and relax `TryBuildLive`'s guard so an equipped no-morph
   humanoid builds from scratch (`BuildMinimalPostIir(2)`) instead of falling back to `Data`. The 2
   entry-runtime spawns stay on `Data`.
5. **Verify:** the whole-DB coverage gate should climb from 824 toward ~984; the tail should shrink to
   ~2. Then an in-client smoke test of a runtime-post-IIR humanoid (make sure it's not bald).

`NpcSpawn.Data` + the splice + `SpawnPlayfieldRewriter` then remain only for the ~2 entry-runtime
spawns (and world objects / create-editor, which are separate).

---

## 3. Commands / gotchas

- Build: `dotnet build ProjectFaolan.slnx`. Tests: `dotnet test Faolan.Tests` (726 green).
- Importer (idempotent except characters): `dotnet run --project Faolan.Importer -- --only npcs`
  (kill server first; `faolan.db` rides along in the commit).
- **Server stop:** by port, never `pkill -f Faolan.dll` (self-match → exit 144). Get pid via
  `ss -ltnp | grep :7010`. `pkill -x dumpcap` is safe (exact name).
- **Loopback capture format** (`*.tshark.json`): top-level `{"Packets":[...]}`, each `{Frame, IsDownload
  (false=c→s / true=s→c), Source/Destination.Port, Opcode, SenderClass, SenderInstance, ReceiverClass,
  ReceiverInstance, BodyHex (body only)}`. Sub-opcode = `BodyHex[8:16]`. (Memory
  `compare_packets_with_retail_dumps` has this.)
- **Login/world-entry wire facts** (retail is leading): client's `0x2003` Authenticate2 carries a TEMP
  clientInstance (`0x0216xxxx`) in both login and create; the auth trio is identical in both; the
  client polls `0x2020` only after it accepts the world spawns; server answers `0x2008`. Our existing
  login resolves the char via `GetCharacterForAccount` (direct match on id, else highest-id fallback);
  `isNewCharacter = Character.Name == null` picks create-editor vs normal login.

## 4. Key files

- `Faolan.Core.Database/Tables/NpcStatCore.cs`, `NpcSpawn.cs`; `DatabaseContext.cs` (1:1 mapping).
- `Faolan.Core.Network/Codecs/Spawn/`: `NpcStatCoreCodec.cs`, `NpcSpawnBuilder.cs`,
  `NpcSpawnAppearance.cs`, `NpcSpawnScaffold.cs`, `SpawnPacketBuilder.cs`.
- `Faolan.Importer/Extractors/NpcSpawnExtractor.cs`, `Captures/ImportOps.cs`, `CapturePetImporter.cs`.
- `Faolan/GameServer/World/WorldBroadcaster.cs` (`SendWorldSpawn` = the per-send token re-stamp).
- Tests: `Faolan.Tests/NpcSpawnFromScratchTests.cs`, `NpcStatCoreCodecTests.cs`.
- Docs: `docs/npc-three-tier-architecture.md`. Memory: `spawn_playfield_token_per_send`.
