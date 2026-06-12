# Project Faolan — open work

The single tracker for what's left. Each area links to its deep-dive doc for the *how*; this file is the *what*.

- **Deep-dive design / RE detail:** `docs/*.md` (linked per section below).
- **Resolved history & detailed diagnoses:** `docs/archive/todo-history.md` (the old TODO, verbatim),
  `docs/archive/chat-regression-handoff.md` (SOLVED), `docs/archive/backpack-inventory-handoff.md` (RESOLVED).
- **Test method for in-client items:** server from `Faolan/bin/Debug` (`dotnet Faolan.dll`, cwd = bin/Debug);
  drive the client with `Faolan.ClientLauncher --console --universeaddr 127.0.0.1:7010 -u faolan -p any -c <char>`.
  Grab `AgeOfConan.log` on any failure (look for `stream bad`, `Dispatcher call ... failed`, `invalid camera
  path`, `does not exist in any RDB`). Restore `git checkout -- faolan.db` after runs.

---

## ⭐ 1. Create-world Kalanthes intro cutscene — TOP PRIORITY

The wash-ashore → Kalanthes cinematic still fails identically across two different world bursts ⇒ the bug is in
the **EDITOR (galley) phase**, not the world burst. The same client plays this intro on retail, so it's
server-side. Full diagnosis: `docs/archive/todo-history.md` §7 + memory `createworld_cutscene_diagnosis`.

- [ ] **Next step:** loopback-capture our create-world flow (ports 7014/7015), `--dump` it, and diff the EDITOR
      stream packet-by-packet against the retail hoxxling editor (`:7018`) to find the first divergence that arms
      the cutscene. (Server-side scry sequence is already verified correct on the right connections.)

---

## 2. In-client verification backlog (harness-passed, need a live client)

These are implemented + unit-tested; they just need a live-client pass. Detailed per-item expectations and the
prior diagnoses are in `docs/archive/todo-history.md`.

**Vitals / stats / movement**
- [ ] `.vitals` (no args → MaxStats), `.vitals 1000 800 500`, `.setstat 36 25` move the bars. (`.hurt`/`.heal` work.)
- [ ] New character's sheet vitals reflect its **class** (mage = more mana, soldier = more health), not the
      level-1 rogue default. → `docs/player-stats.md`
- [ ] Login/movement still spawn at the correct position after the raw-body float-endianness fix. → `docs/archive/todo-history.md` §8

**Combat** (loop both directions) → `docs/archive/quests-and-combat.md`, memory `protocol_stats_and_combat`
- [ ] Normal-attack FSM re-test: the swing-1 BccAck now matches retail byte-for-byte (u32 argsLen fix, field3
      phase splice, 0xCB constant). Confirm attacks no longer go random/self after the first swing.
- [ ] NPC **counter-attack** (the second ack retail sends for the mob's own behaviour, actor = target handle) —
      not yet reproduced; mob may not visibly fight back.
- [ ] Kill an NPC → despawn + XP + possible loot bag; enough XP → level-up (level + full-vitals push).
- [ ] Stand near a hostile NPC → it aggros and hits you (~2s); let it kill you → respawn (~10s) at nearest
      resurrection point; walk near an undiscovered point → "Resurrection point discovered".

**Mounts**
- [ ] `.mount` / `.mount 3BAD7D` — currently does nothing; report cast bar / horse spawn / ride. Likely needs the
      mount-entity spawn.

**Teleport / maps**
- [ ] `.teleport 2010|2040|3000|4010` — moves the player + buff but the **map doesn't change** (soft same-conn
      map-change vs retail's reconnect). → fix the map render on teleport.
- [ ] Maps with a world burst (2000/2010/2040/3000/4000/4010) render; out-of-range maps 1000/11040 now import —
      verify they render or fall back cleanly. → `docs/archive/todo-history.md` §9
- [ ] `.maps` / teleport labels show real RDB names (4010 = "Tortage Island", etc.).

**Conversations** → memory `protocol_conversation_routing`
- [ ] Re-test a beach login: Casilda gives HER dialogue, Kalanthes his; advancing to an un-captured step now
      CLOSES the window cleanly (no chat-lock) instead of hanging. "No lines / wrong branch" on un-captured
      quest-states is a **content-coverage gap** (proper fix = RDB Lua/tag VM, deferred — see §6).

**Inventory / loot** → memory `protocol_containeradditem`
- [ ] Backpack shows the seeded `CharacterInventory` items; moves / drops / stack-splits survive relog; chat stays
      connected. (Delivered post-load; beach snapshot built from scratch — both default on.)
- [ ] Click a dropped loot bag → loot window opens, bag stays; close/expire → it fades. (Taking items into the bag
      is client-side; no server add-item packet exists.)

**Quests** → memory `protocol_quest_state`
- [ ] `.quest add 13479 5 Scavenger Lackey` → journal entry; kill 5 → goal advances + completes; persists across
      relog. `.quest list` / `.quest remove <id>`. (Accept-from-dialogue is VM-gated, see §6.)

**Cross-player (needs TWO clients)** → `docs/archive/server-architecture-roadmap.md`
- [ ] Two players on the beach see each other spawn, move, and despawn (logout / out-of-range). Open question:
      does the client enforce the despawn playfield token? → memory `protocol_despawn_n3toclientquit`

---

## 3. Construct world/NPCs from the DB (spawn-dissection follow-ups)

Byte-level dissection of every world-packet family is COMPLETE and round-trips byte-exact; what remains is
construction-from-DB + cross-session normalization, gated on in-client validation or fresh captures.
Full detail: `docs/archive/spawn-dissection-plan.md`.

- [ ] Drive arbitrary `NpcSpawns` / `WorldObject` rows by re-addressing the cross-session body (handle / scaffold /
      instance refs + the chest `stateHandle` / interaction). Byte-identical wiring ships; cross-session
      reconstruction is the in-client gate.
- [ ] Inventory add/remove client stall — semantic / client-side, not a packet defect.
- [ ] MoveTask mode-00 "approach/face" param floats + a `PatrolPath` table to source waypoints.
- [ ] Fold the pre-IIR / post-IIR scaffolds into rows.
- [ ] Appearance: generate the morph entries from `CharacterBody` (the editor sliders → float-valued scaffold
      entries) instead of the Dikketest defaults. → `docs/archive/player-spawn-generation-plan.md` (COMPLETE; this is the
      one optional remainder)

---

## 4. Live world simulation (architecture roadmap)

The protocol is done; the milestone-1 simulation foundation (in-memory world model + 10 Hz tick + interest
management + combat/regen/death/respawn/AI) is in and harness-verified — the in-client payoff is the cross-player
and combat-loop items in §2. Forward milestones (shared zones, persistence, scaling) are in
`docs/archive/server-architecture-roadmap.md`.

---

## 5. Content from the client RDB

Item templates + dialogue text are cracked (TEA-ECB, memory `rdb_extraction`); the strategy shift from
"scrape captures" to "import real client data" is unblocked for the remaining gaps. Full plan + index format:
`docs/archive/rdb-extraction-plan.md`.

- [ ] NPC / creature definitions (real max HP, replacing the spawn-seeded value).
- [ ] Spell / ability definitions (names + params) — also unblocks real spell-grant (no capture has it; needs a
      fresh caster capture OR RDB import). → `docs/archive/todo-history.md` §3 "spells blocked on data"
- [ ] Tortage cinematic / camera-path resources (ties into §1).
- [ ] Playfield / map / spawn-template data → reconstruct world bursts instead of replaying packets.

---

## 6. RDB-driven dialogue VM (deferred — quest-engine-sized)

⛔ **PINNED.** Conversation branching is Lua (`LuaArchive`) keyed on quest-state **tags**
(`player:HasTag/AddTag`), text in `TextStrings`; there's no static conversation-tree resource. Needs a Lua + tag /
quest VM (entangled with quests). The shipped capture-replay stage machine is the interim. Defer until a quest /
tag system exists. → memory `lua_vm`, `protocol_conversation_routing`; `docs/archive/rdb-extraction-plan.md`.

---

## 7. Codebase / refactoring

- [ ] **HIGH VALUE — embedded capture data out of source code.** `CreateFlow/` is ~8.8k lines, 80%+ raw hex
      byte-literals (`RetailCreateWorldEntryBurst.cs` alone = 6,957 lines). Move it to a DB table + importer
      (mirror `MapWorldBurst` / `IirRecordCodec`) with a **byte-equality test (DB-loaded == embedded arrays)** as
      the safety net, then delete the embedded file. Catch: the burst weaves the live instance id into ~324
      packets via `AppendVarint` — needs a template-with-substitution capture, not a flat blob. Verifiable
      without the client (the byte-equality oracle).
- [ ] Fold `Packets.cs` sender-bytes duplication into `World/WorldFraming` (verify which conversation senders are
      truly identical before merging). Small, safe.
- [ ] Rename `Packets/FullCharSnapshot/BigboyTailsFile.cs` to something descriptive. Trivial.

---

## 8. Lower-priority / future

- [ ] **Real password validation** — login is DH + TEA-CBC; we accept any password (validation stubbed). Needs a
      client DH-key binary patch + C# port of the cipher. Fully understood, not implemented. → `docs/archive/login-decryption-plan.md`
- [ ] **Recover real opcode / RPC names** — feasible (names are in `AgeOfConan.exe`); map recovered names → wire
      ids. → `docs/archive/opcode-re-plan.md`, `docs/aoc-opcode-ids.md`, `docs/aoc-client-rpc-methods.md`

---

## Known non-blockers (no action unless curious)

- `OverrideGameTweaks` (`0x2085...`) — client logs `stream bad after 'overrides'` and drops that one route every
  session; harmless, pre-existing.
- Analyzer tail-drop on ~3 GameServer streams (a few KB short vs ground truth); doesn't affect gameplay.
  → memory `analyzer_tcp_reassembly_gap`
