# NPC spawn / Casilda render — handoff (2026-06-13)

Paste-ready prompt for the next session, plus the state of play. Goal: **make beach (map 4010) NPCs
render like retail** — specifically (a) Casilda (a chained quest-captive) is invisible, and (b) NPCs lack
floating name tags + healthbars (retail floats name+HP over every NPC; on Faolan only Kalanthes shows a
frame name).

## Current state (committed, stable)
- Branch `development`, HEAD around `fae8d4d`. Server runs clean; `FAOLAN_NPC_INDIVIDUAL_SPAWN` is **off by
  default** (the burst path, ~retail-shaped, no crash). `faolan.db` is dirty from playtesting — `git
  checkout -- faolan.db` to clean.
- Run: `cd Faolan/bin/Debug && dotnet Faolan.dll`. Drive client: see CLAUDE.md. Client log:
  `/var/games/debian-installation/steamapps/common/Age of Conan/AgeOfConan.log`.
- Loopback captures of OUR output: `Captures/loopback*.pcapng` (+ `.tshark.json`). Retail reference:
  `Captures/account-stricken997-character-randochar-login-kalantes_chat.*` (Casilda renders chained there).

## What we proved this session (don't re-derive)
1. **Casilda's spawn bytes are not the problem.** Her FA015807 IIR is byte-identical to the retail capture
   where she renders (only transient `NpcStats.SpawnAnimation` differed; normalized to 0 via
   `NpcSpawnBuilder.TryBuild(..., freshSpawn:true)`). She is delivered (was sent twice; phantom dup at pos
   448 now deduped). Pre-IIR/IIR differ from retail only the way Pillager (which renders) also differs.
2. **Content is NOT the problem.** Same client + content proxy renders everything on retail. Ignore the
   `VisualMesh/RDBMonsterData/ResourceManager … does not exist` errors in AgeOfConan.log — red herrings.
3. **The `n3ToClientQuit … e_Impossible` client warning is benign** — our despawn body is byte-identical to
   retail's; sender instance (0x58 vs retail 0x08/0x0b) makes no difference and is not validated.
4. **The loader crashes are a packet flood**, not bad bytes:
   - flag ON streams the whole accumulated model (~98 NPCs) *during* the loading screen → crash.
   - flag OFF: the per-NPC `96C46740` MoveTask pairing (now reverted) doubled interest packets → crash on
     first move. Reverted; default path is back to ~139 packets (worked pre-session).
5. **Root cause of the flood = burst-vs-interest DOUBLE-SPAWN.** `BroadcastPlayerEntry` is supposed to seed
   the burst's NPC handles into the player's `Known` set so interest doesn't re-stream them, but it doesn't
   take (first interest pass goes 0 → `now knows 98`). So the first move re-streams all ~97 in-range NPCs.
6. **The model is the union of all capture sessions** — 491 `NpcSpawns` rows (map 4010) → 168 model → ~98 in
   range, i.e. ~4× retail's NPC count (35 crocs vs 7). The burst replays ONE session (~43); the
   individual-spawn path streams the union.

## Next steps, in order
1. **Fix the double-spawn / Known-seeding** (`Faolan/GameServer/World/WorldBroadcaster.cs`
   `BroadcastPlayerEntry`, ~line 120; `RetailLoginReplay.RecordSpawnedNpcName`). Figure out why
   `SpawnedNpcNamesByHandle` is empty / handles mismatch when seeding runs (ordering vs the burst replay, or
   burst wire-handle ≠ model `CapturedHandle`). After: first move should stream ~0 burst NPCs. This kills the
   flood and is the prerequisite for everything else.
2. **Then re-test the individual-spawn flag** (`FAOLAN_NPC_INDIVIDUAL_SPAWN=1`) — but first add cross-session
   **mob dedup** so the model is retail-shaped (~43, not 168). Friendly NPCs are already deduped by name in
   `EnsureZoneNpcs`; mobs still pile up by cell across sessions.
3. **Casilda render**: with a clean single spawn, do a **field-level IIR diff** of our Casilda vs retail
   (there was a ~1-byte/off-by-one shift in the IIR region — chase whether a field is dropped/added in our
   re-encode). Reuse the `ProtoCoreCharacterIIR` typed decode pattern (a throwaway test in `Faolan.Tests`
   that reads `loopback.tshark.json` + the randochar dump via `TsharkCaptureReader` and dumps named fields).
4. **Nameplates**: why does only Kalanthes (NpcFlags=1) float/show a name while mobs (NpcFlags=0x1C0000)
   don't? Retail floats names for all. Verify against the retail capture whether it's NpcFlags, a missing
   registration packet, or a client UI thing.

## Also open (separate, tracked in TODO §5)
- `GrantStartingAbilities` grants 0 for most classes — `ClassAbilities` only has 3 rows; re-run the importer
  with `--archive`. AND the class-id mapping is wrong (Testertwo is a Barbarian but stored as class 41 =
  Necromancer).

## Paste-ready prompt
> Continue the Age of Conan NPC spawn work (read `docs/npc-spawn-handoff.md` and TODO §3 first). Beach NPCs
> on map 4010 don't render like retail: Casilda (chained captive) is invisible and NPCs lack floating
> name+healthbar tags. We proved Casilda's spawn bytes match retail and content isn't the issue. Start with
> task #1: fix the burst-vs-interest double-spawn (Known-seeding in BroadcastPlayerEntry) so the first move
> stops re-streaming the burst's NPCs — that's the packet flood behind the loader crashes. Don't restart the
> live server or have me crash my client without warning me; use the loopback/retail captures in `Captures/`
> for diffs.
