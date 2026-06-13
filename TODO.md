# Project Faolan — open work

The single tracker for what's left. Each area links to its deep-dive doc for the *how*; this file is the *what*.

- **Deep-dive design / RE detail:** `docs/*.md` (linked per section below).
- **Resolved history & detailed diagnoses:** `docs/archive/todo-history.md` (the old TODO, verbatim),
  `docs/archive/chat-regression-handoff.md` + `docs/archive/chat-handoff.md` (both SOLVED),
  `docs/archive/backpack-inventory-handoff.md` (RESOLVED). FullChar snapshot framing: `docs/db-container-handoff.md`.
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
- [ ] **Health regen not visibly working in-client** (Melvin, 2026-06-13): the tick HAS a regen pass
      (`WorldSimulation.RegenerateConnectedPlayers`, 1 Hz, ~5% hp / 10% stamina / 5% mana per sec, `PushVitals`),
      but HP doesn't visibly come back after taking damage. Check that combat damage and the regen pass mutate the
      SAME `WorldEntity.Vitals` instance (a split instance would regen a different copy than combat damaged), that
      the 0x96B8DC59 vitals push updates the HUD bar, and tune the provisional rates. Ties into the combat-vitals
      wiring (kill/XP/death loop).
- [ ] `.vitals` (no args → MaxStats), `.vitals 1000 800 500`, `.setstat 36 25` move the bars. (`.hurt`/`.heal` work.)
- [ ] New character's sheet vitals reflect its **class** (mage = more mana, soldier = more health), not the
      level-1 rogue default. → `docs/player-stats.md`
- [ ] **XP resets on relog** (Melvin, 2026-06-13): in-game XP is not persisted across logout — it reverts to 0 (or
      the snapshot default) on the next login. `character.Experience` is patched into the snapshot tails
      (`CurrentXp`, idx 80 ÷10) but is evidently never SAVED when it changes (kill/XP loop in §2). Persist
      `Character.Experience` on gain (and on logout) so it survives relog. RELATED: the XP **max / to-next-level**
      is still the captured template's (Strie's) value — wrong for the character's level — so even the bar is off.
- [ ] **In-game CLASS is wrong — and it is NOT in the snapshot** (Melvin, 2026-06-13; VERIFIED). A created Dark
      Templar shows the wrong class. Exhaustively ruled out: the class id is not a plain field in any world packet
      (21 dumps / 5 classes, every sub-opcode+offset ×1/×2), not in the tails `Values` table (index 78 = ARCHETYPE
      only: Mage=2/Rogue=3), and not in the access-window entries (those are playfield access). **Proven by
      injection**: swapping in a different-class (Herald) tails did NOT change the displayed class. So the in-world
      profession comes from char-select, the player spawn, a feat/profession packet, or client-side derivation —
      finding it needs that angle (likely client RE), NOT more snapshot decode. → memory `snapshot_tails_construction_scope`
- [ ] Login/movement still spawn at the correct position after the raw-body float-endianness fix. → `docs/archive/todo-history.md` §8
- [x] **Chat broken again — FIXED (Melvin, 2026-06-13)**: chat + character names (floating + healthbar) work again
      in-client. TWO root causes, both fixed this session:
      - **#2 (the chat blocker), commit 5c7f2a8**: the DB-container world-entry snapshot
        (`FAOLAN_WORLDENTRY_DB_CONTAINERS`, was default ON) rebuilt the `0x737A6DF9` root into a blob the client
        REJECTS — `AgeOfConan.log`: "N3InfoItemRemote::Construct: ReadSubClass for IIR 1937403385
        (FullPlayerCharacterIIR) failed! … Blob data may be inconsistant, data invalid". The client can't construct
        the player's OWN character, so world entry never completes → names don't render → no CommunityServer (chat).
        The hint was the client log past the RDB noise. Interim fix (5c7f2a8) defaulted to the verbatim snapshot to
        restore chat; the REAL fix (85c2886) is the BE32 root-length framing — DB containers are back on. Verified:
        FullPlayerCharacterIIR error gone, client reaches "Authenticating with CommunityServer", names render. See the
        DB-container item below.
      - **#1 (the loading hang), commit 5c99a6a**: client flooded `RequestAggroStatus` (GameCharAgentProject
        `0x5B9756FC` method 0x2000) ~450× because we mis-dispatched it (read `[pbLength][pbOpcode]` off a body that
        is `[0xC350][player][0xC350][target]`, so "pbOpcode" == player instance → dropped as a client-ping). Routed
        the class by id (like Tradepost), reply `[0xC350][target][0]`. Flood 450→19 (retail=17), post-entry now a
        healthy idle. → `HandleGameCharAgentProject.cs`, memory `protocol_gameserver_rpc_multiplexing`.

- [x] **DB-container world-entry inventory — FIXED via Ghidra (2026-06-13), DEFAULT ON, unconditional.** In-client
      verified: chat connects, names render, bag shows the player's real DB items. Root cause: the `0x737A6DF9`
      protobuf root is **length-prefixed by a 4-byte BIG-ENDIAN length** the server wrote wrong on resize (the old
      `AdjustSnapshotPrefixRootLength` was a byte-misaligned varint model — `varint−rootLen=4483` was a coincidence of
      two near-equal-length captures). Decompiled `AgeOfConan.exe`: `n3InfoItemRemote::Construct` → `FUN_00cbfb80`
      reads `[u32 BE blobLen][blob]`, then `ReadSubClass` reads the `0x18` version; `blobLen = field5Len + rootLen`.
      FIX = `GameServerListener.SetSnapshotBlobLength` (writes BE32 at prefix offset 1), called by
      `BuildBeachInventoryPacket`. Framing in `docs/db-container-handoff.md`; decompiles in `docs/re/`; golden test
      `Faolan.Tests/SnapshotBlobLengthTests.cs`. (`FAOLAN_WORLDENTRY_DB_CONTAINERS` env flag removed.)

      (historical detail kept for reference) The client never opened a TCP socket to **AgentServer (7012)** even
      though the PlayerAgent handoff for it is well-formed (frame 125 in `Captures/loopback2.pcapng`: discriminator
      `fae5989d02`=AgentServer, addr `127.0.0.1:7012`) — because world entry never completed (invalid FullChar above).
      Memory `protocol_chat_connection_regression`, `protocol_agentserver_chat`.

**Combat** (loop both directions) → `docs/combat.md` (BCC ack protocol), `docs/archive/quests-and-combat.md`, memory `protocol_stats_and_combat`
- [x] Normal-attack FSM re-test: the swing-1 BccAck now matches retail byte-for-byte (u32 argsLen fix, field3
      phase splice, 0xCB constant). VERIFIED in-client 2026-06-12 (Broken Oar equipped): armed swings land,
      no random/self-target. NOTE the earlier "broken attacks" were UNARMED (see next item).
- [x] **Equipment window + bag show the player's DB items** (2026-06-12 `20f2cc1`, BE32-framing fix 2026-06-13
      `85c2886`): the world-entry snapshot carries DB containers unconditionally - the old chat-gate constraint was
      the dead retail CDN, lifted by the ContentMirror. In-client verified incl. equip-from-bag + persistence. En
      route: Socket partial-write truncation fixed (24fbed6); the post-load second-snapshot delivery is dead (the
      client rejects/ignores re-constructs for an already-built identity).
- [ ] **Bag-poison items**: an item only ever captured WORN (never in an inventory container) renders the
      WHOLE bag empty when placed in CharacterInventory (repro: 13826 Combatant's Hauberk; client shows
      VisualMeshLoader 1010001 misses). Likely missing local bag/icon resources. Detect/exclude such items in
      .additem + add_item, or solve the resource gap (mirror pre-seed / template decode).
- [ ] **Mid-session item delivery does not exist**: a second 0x737A6DF9 is rejected/ignored by the client, so
      .additem/add_item items appear on the NEXT LOGIN only. Find retail's live-add mechanism (if any) or
      surface the relog requirement in the tools.
- [x] **Equip/unequip persistence + acks** (2026-06-12, commit 226b16f): 0x09 grammar decoded from capture
      (TypeId = window pair incl. live 41842/41843, Mode = action counter, tail = [src][dest][item][item][0][flags]);
      both pairs persisted + retail's ack (identity quad / null filler) + 0x201C entry (0x65/destSlot, 0x68/0).
- [x] **Unarmed attacks** (2026-06-12, commit 226b16f): Interact 0x11 echo-acked (phase = request Mode, no
      result block - byte-exact golden tests); targeted unarmed damage rides the armed path (extension,
      in-client check pending).
- [ ] **Melee swings are cone/AoE**: an AoC melee swing damages every target in the arc, not just the selected
      one (Melvin, 2026-06-12). Current server resolves only SpellTarget.TargetHandle; needs arc/range target
      selection from the world model.
- [ ] **No-target melee hits SELF (no damage)** (Melvin, 2026-06-13): swinging with NO target selected plays the
      blood/hit animation **on the player** but deals no damage. The attack FSM is resolving the empty
      SpellTarget.TargetHandle (0) back to the caster's own handle (or defaulting target=self) and sending a hit
      reaction to self. Should resolve to "no target -> swing whiffs" (animation only, no self hit-react), or to
      arc target-selection (see cone/AoE item above). Server attack FSM, NOT ContentMirror (memory
      `feedback_combat_not_contentmirror`).
- [ ] NPC **counter-attack** (the second ack retail sends for the mob's own behaviour, actor = target handle) —
      not yet reproduced; mob may not visibly fight back.
- [ ] **Add `.kill` GM command + MCP `kill` tool** (Melvin, 2026-06-13): kill the currently SELECTED target
      (the player's last LookAt/select handle, `Session.SelectedTarget` or equivalent), INCLUDING self if the
      player has themselves selected. Reuse the combat death path (`ApplyNpcDamage`/`OnNpcKilled` for NPCs,
      vitals->0 for the player). If no target is selected, send a system message ("no target selected") rather
      than no-op. Expose via MCP (`kill` tool routing through `RunGmCommand`).
- [ ] **BLOCKER (PINNED — needs client-side RE) — mid-session entity spawn doesn't render** (`.spawnnpc`,
      `.spawnbag`/kill loot bags, `.mount`, interest streaming). Full writeup: **`docs/mid-session-spawn-blocker.md`**
      / memory `protocol_midsession_spawn_blocked`. Decoded 2026-06-13 via loopback captures + the necrol80 mount
      capture. **Render mechanism:** `FA015807` spawn → client POLLS `0x2020` for the handle → server `SpawnCheck
      0x2008` → renders. Our client polls + renders the **world-entry burst** (264 SpawnCheck exchanges for
      `0x000818xx` handles) but **never polls mid-session** — it refuses to register our pushed `FA015807` (capture
      showed only `4517aa81` movement, zero `0x2020`), whereas retail's client registers + polls the same kind of
      push (the mount). The packet is byte-correct; it's a **client load-vs-play gate** that rejects mid-session
      entity registration from us (same root as `n3ToClientQuit sent from 0 -> e_Impossible`). RULED OUT: framing
      (identical to the burst, consistent `0x58`), handle range (real bug; allocator `0x40000000`→`0x0F000000`→
      **`0x00090000`**, the retail `0x000286xx` range — necessary, NOT sufficient), body bytes, missing-precursor.
      **NEXT = Ghidra on the FA015807/interest dispatch + the play-mode conversation/object-set validation** —
      packet analysis is exhausted. Until then only the burst renders; corpse-stays-on-loot is in but moot.
- [ ] Kill an NPC → despawn + XP + possible loot bag; enough XP → level-up (level + full-vitals push).
- [ ] Stand near a hostile NPC → it aggros and hits you (~2s); let it kill you → respawn (~10s) at nearest
      resurrection point; walk near an undiscovered point → "Resurrection point discovered".

**Mounts**
- [ ] `.mount` / `.mount 3BAD7D` — **does NOT work** (confirmed in-client 2026-06-13): the server-cast ApplySpell +
      SpawnMountForPlayer push renders nothing (the mount-entity spawn is gated on the mid-session-spawn blocker,
      `protocol_midsession_spawn_blocked`). In-game a mount is a learnable **spell/ability the PLAYER casts**, not a
      server push.
- [ ] **Import the mount spell/ability from the necrol80 capture so it can be GRANTED + cast.** The capture
      `account-faolan-character-necrol80-login-mount_move_dismount` has the full mount summon (ApplySpell 0x6B87E316
      / spell 0x3BAD7D, the F508F4C1 channel packets, and the "Fading Black Horse" FA015807 spawn at idx 1523). The
      importer doesn't harvest the mount's grant identity (Data quad + slot), so `.grantspell` can't grant it (not
      in the Abilities table; only the 26 captured combat 0x2052 grants are). Fix the importer to pull the mount
      ability out of this capture, then `.grantspell <mountId>` puts it on the bar and the player casts it — the
      correct, client-initiated mount flow (and a clean test of whether a client-CAST spawn renders mid-session).

**Teleport / maps**
- [ ] **`.teleport` is BROKEN (Melvin, 2026-06-13): it does NOT move the character.** Instead it makes the char
      level 80 and grants a bunch of spells (almost certainly a captured leveled-char SNAPSHOT being replayed
      instead of a map-change) — the character stays put. Find where `.teleport` (DoTeleport) replays a snapshot
      and make it actually relocate the player (+ map change). Supersedes the "map doesn't change" note below.
- [ ] `.teleport 2010|2040|3000|4010` — (historical) was meant to move the player + buff but the **map doesn't
      change** (soft same-conn map-change vs retail's reconnect). → fix the map render on teleport.
- [ ] Maps with a world burst (2000/2010/2040/3000/4000/4010) render; out-of-range maps 1000/11040 now import —
      verify they render or fall back cleanly. → `docs/archive/todo-history.md` §9
- [ ] `.maps` / teleport labels show real RDB names (4010 = "Tortage Island", etc.).

**Conversations** → memory `protocol_conversation_routing`
- [x] **Repeated NPC talks work** (Melvin, 2026-06-13, in-client): clicking Kalanthes repeatedly opens his
      dialogue every time. ROOT CAUSE was that the lookup keyed on `Mode`, which is a per-SESSION counter the
      client increments every click (2,3,4,…,24) — NOT a quest-state; it drifted past the captured Mode 1/2/3 and
      every match missed after the first couple of talks. FIX: `GetConversationNodeByName` tries the exact
      4-tuple, then falls back to the same `(NpcName,TypeId,Field3)` at the lowest captured Mode. The reply Mode
      is NOT echo-validated by the client (the fallback renders fine). memory `protocol_conversation_routing`.
- [x] **Multi-screen dialogue chains** (2026-06-13): "only the first dialog screen showed" was NOT missing
      capture data nor an importer bug — the deeper screens ARE imported, as sibling nodes keyed by an ascending
      Field3 (Kalanthes Mode 1: Field3 0 = greeting, Field3 1 = next screens). The runtime only stage-advanced
      WITHIN the single greeting node (1 stage), so a KnubotAnswer exhausted it and closed. FIX: when a node's
      stages run out, `SendNextConversationStage` chains to the next Field3 sibling (`GetNextConversationNode`,
      session tracks the active NpcName/TypeId/Mode/Field3/handle). Verified the chain exists in the DB; NEEDS
      IN-CLIENT: pick an option in Kalanthes' dialogue → the next screen should appear instead of closing.
      (Still linear-replay: it ignores WHICH option is picked; true branching = the §6 Lua/tag VM.)
- [~] **Per-option branching — answer-index dialogue TREE BUILT (2026-06-13), needs DB populate + in-client.**
      Conversations are now modeled as a tree keyed by `(NpcName, TypeId, AnswerPath)` ("" = greeting, "0",
      "0>1", …): the `KnubotAnswer` option index is the branch selector, and the branches captured across a
      conversation's re-walks UNION by answer-path. SHIPPED: `TsharkCaptureReader` (rebuilds byte-exact packets
      from `*.tshark.json`, CRC-verified), the tree extractor + Merge (`ConversationExtractor`), schema
      `ConversationNode.AnswerPath` + migration `ConversationAnswerPathTree`, and the runtime path-router
      (`ServeScreen`/`AdvanceConversation`/`TryReplayConversation` route by answer index; stage-segmentation +
      Mode/Field3 retired). 291 tests green incl. new tree-extractor tests. Grammar/format in memory
      `protocol_conversation_routing`.
      - [ ] **DB populate (Melvin's tshark re-dump in progress):** after every conversation capture is a
            `*.tshark.json`, run the importer (`dotnet run --project Faolan.Importer -- --skip-characters`) to
            rebuild the trees; restart the server first so the migration applies. NB the running server is still
            on the OLD linear-chaining build — rebuild + restart to switch.
      - [ ] **In-client:** pick each option at each Kalanthes screen → every captured branch advances to its own
            screen (not just the one the old linear replay followed). Depth/coverage is bounded by what the dump
            walked (walk every option). Still no true VM (un-walked branches close gracefully); that's §6.
- [ ] Re-test a beach login: Casilda gives HER dialogue, Kalanthes his; advancing to an un-captured step now
      CLOSES the window cleanly (no chat-lock) instead of hanging. "No lines / wrong branch" on un-captured
      quest-states is a **content-coverage gap** (proper fix = RDB Lua/tag VM, deferred — see §6).

**Inventory / loot** → memory `protocol_containeradditem`
- [x] Backpack shows the `CharacterInventory` items; equip/unequip + bag moves persist and survive relog; chat
      stays connected. VERIFIED in-client 2026-06-12 (delivery = DB containers in the WORLD-ENTRY snapshot,
      commit 20f2cc1; the post-load path is retired - `docs/inventory.md`). Stack-splits still unexercised.
- [ ] DB containers on a NON-beach map: enter world / `.teleport` on another captured map → bag + doll show the
      DB items, no IIR errors, chat connects (the substitution applies to every map's burst snapshot; only the
      beach is verified). ALSO check a captured/leveled char (e.g. Hoxxling) renders its gear correctly.
- [ ] From-scratch TAILS (BeachSnapshotPrefix/BeachTailsBuilder, golden-tested but no longer on the live path
      since the post-load cleanup 135b06f): future construct step - replace the captured tails in the
      world-entry snapshot, then re-verify level/vitals/progression UI on fresh AND captured chars.
- [x] **`DispatchAddItem` (s2c 0x2020) builder — the live add packet EXISTS** (corrects the old "no server
      add-item packet" belief, RPC-opcode recovery §1). Decoded from the strickenbla questcomplete capture,
      byte-exact golden test `DispatchAddItemTests`; body = `[ID32 invType,charId][u32 pos][u32-len entry][u32
      feedback]`, entry = the bare `ProtoInventoryGameEntry` (snapshot `.Item`). Wired into a shared
      `GrantItemToBackpack` (persist + live add) reused by `.additem`, loot, and `.spawnbag`.
- [x] **IN-CLIENT VERIFIED 2026-06-13: the live add renders mid-session.** A connected player (Darktemplarchar)
      received ~8 `.additem`/`add_item` gear pieces live (no relog), saw them in the bag WITH readable level
      requirements, kept them, and chat stayed connected throughout — i.e. the bag rendered, no IIR error, session
      intact. Overturns the long-standing "snapshot is the only delivery" belief. (Worn-only items in a bag still
      risk the relog bag-poison; level-appropriate selection is gated on RDB item levels not yet in the DB.)
- [ ] **Live item REMOVE — `DispatchRemoveItem` (s2c 0x201F)**: build the counterpart to `DispatchAddItem`, so an
      item can be pulled from a connected player's bag mid-session (there is currently NO way to undo an `.additem`
      live). Opcode is in the recovered s2c table (`GameCharInterfaceDispatchOpcodes` / `docs/aoc-rpc-method-opcodes.md`);
      body fields `i_cInventoryID, i_nItemPos, i_nAmountToRemove`. Decode against a capture, golden-test like
      `DispatchAddItemTests`, then wire a `.removeitem`/`remove_item` MCP path that clears the `CharacterInventory`
      row AND pushes the live remove. Also surfaces the bag-poison cleanup need (worn-only items in a bag).
- [ ] Click a dropped loot bag → loot window opens, bag stays; `loot all` grants its items (live `DispatchAddItem`)
      + coins (`TryLootCash`); close/expire → it fades. Bags now carry random items (kill drop + `.spawnbag`); the
      loot-WINDOW item list display (DispatchReplyLootForCharacter*) is still undecoded — grant works via loot-all.

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

- [~] Drive arbitrary `NpcSpawns` / `WorldObject` rows by re-addressing the cross-session body (handle / scaffold /
      instance refs + the chest `stateHandle` / interaction). Byte-identical wiring ships; cross-session
      reconstruction is the in-client gate.
  - [x] **Model-driven NPC streaming (2026-06-13)**: the world burst only renders the NPCs near the capture's
        vantage point (beach burst = 42 of map 4010's ~36 distinct NPCs; Casilda was outside it). NPCs now stream
        from the world model via interest management as the player moves: `InterestManager.ResolveMove` gained
        `includeNonPlayers` (on for moves, off at entry to keep the load tight); burst NPCs are seeded into the
        player's `Known` set at entry so they're never doubled; `EnsureZoneNpcs` dedupes cross-session re-captures
        by (name, 8-unit position cell) with burst-handle rows winning their cell (so the kept entity coincides
        with the burst-rendered one). Conversation routing names are registered for streamed + in-range model NPCs
        (`SpawnedNpcNamesByHandle`), re-affirmed from the model at entry so a burst NPC whose DB-rebuilt body read a
        wrong/missing name still routes (the likely "can't talk to Kalanthes" cause). Unit-tested
        (`InterestManagerTests`, 7). **IN-CLIENT GATE:** confirm Casilda renders after the player moves, and that
        clicking Kalanthes opens his dialogue (a new `TalkToNpc: no conversation node ...` log line names the exact
        handle/name/typeId/mode/field3 if the click still closes the window). Residual: unique NPCs captured at
        spots >8 units apart (Casilda ~87u, one extra Kalanthes ~35u) still render as 2 — a NpcSpawns data-quality
        artifact, not a streaming bug.
  - [x] **NPC spawn / loader crash — SOLVED in-client (2026-06-13, session 3).** A freshly-created character logs
        in, the loading screen completes, and the beach renders with NPCs **including Casilda**. The crash chain was
        three bugs, all fixed (`docs/archive/npc-spawn-handoff.md` for the full diagnosis):
    - **The first-move spawn FLOOD** (the loader crash) was the entry `Known`-seeding being wiped: `BroadcastPlayerEntry`
      ran a PLAYER-ONLY `ResolveMove` (`includeNonPlayers=false`) whose despawn-prune removed every NPC handle it had
      just seeded, so the first move re-streamed the whole zone (~95). Fixed: the player-only pass no longer prunes
      NPCs, entry seeds EVERY in-range NPC into `Known`, and model NPC handles align to the burst's wire handle BY
      POSITION (not the session-volatile handle). First move now streams ~0.
    - **The loading-screen crash** was a divergent burst: cross-session `NpcSpawns` rows carry foreign-session
      instance refs, so a non-byte-identical DB rebuild builds an entity the client can't realize (`pcDynel is NULL`).
      Fixed: the burst keeps the VERBATIM captured body unless the DB rebuild is byte-identical. (Also reverted the
      session-2 `freshSpawn` re-encode of the burst, which had re-shaped it.)
    - **Casilda renders** via the model/interest stream (she is outside the burst vantage); the `freshSpawn`
      SpawnAnimation-normalize stays only on that from-model path.
    - Residual (cosmetic, open): floating nameplates over mobs (only frame names show); unique NPCs captured >8u
      apart still render as 2 (NpcSpawns data-quality, not a streaming bug). The `n3ToClientQuit … e_Impossible`
      warning is benign.
- [ ] Inventory add/remove client stall — semantic / client-side, not a packet defect.
- [ ] MoveTask mode-00 "approach/face" param floats + a `PatrolPath` table to source waypoints.
- [ ] Fold the pre-IIR / post-IIR scaffolds into rows.
- [ ] Drop the inventory-snapshot template dependencies (`BuildDbInventorySnapshot` still consumes the captured
      `0x737A6DF9` packet; kept per-session as `Session.InventorySnapshotTemplate` for the `.additem` mid-session
      rebuild). Per remaining dependency type:
  - [ ] **Framing** (RPC header bytes + subOpcode/c350/instance copied from the verbatim packet) — trivially
        buildable via `WriteHeader` + the session's instance; no decode needed, just do it.
  - [ ] **Typed protobuf root** (`ProtoFullCharacterIIR` parsed from the captured root; only `Inventories` is
        regenerated) — author the remaining root fields from character rows (round-trip is 37/39; see memory
        `fullchar_generation`).
  - [x] **Prefix + tails** — beach-map gate REMOVED 2026-06-12: the scratch path (`BeachSnapshotPrefix` +
        `BeachTailsBuilder`) now runs on every map, with the live map's playfield token lifted from the captured
        tails and stamped over the template's baked-in beach token (`WorldDespawn.TryReplacePlayfieldToken`;
        no extractable token = keep captured tails). Remaining: author the per-char `UnknownBefore` progression
        region (739 B → 441 KB blob) + the prefix quest-count field; see memory
        `snapshot_tails_construction_scope` and the in-client check in §2.

  Once all three are server-owned, `.additem`/post-load can build the snapshot with zero captured bytes and the
  session template becomes dead code.
- [ ] Appearance: generate the morph entries from `CharacterBody` (the editor sliders → float-valued scaffold
      entries) instead of the Dikketest defaults. → `docs/archive/player-spawn-generation-plan.md` (COMPLETE; this is the
      one optional remainder)

---

## 4. Live world simulation (architecture roadmap)

The protocol is done; the milestone-1 simulation foundation (in-memory world model + 10 Hz tick + interest
management + combat/regen/death/respawn/AI) is in and harness-verified — the in-client payoff is the cross-player
and combat-loop items in §2. Forward milestones (shared zones, persistence, scaling) are in
`docs/archive/server-architecture-roadmap.md`.

### 4b. Named-but-unhandled client packets (surfaced by the opcode mapping, 2026-06-12)

- [x] **Recover the 16-bit RPC method opcodes via Ghidra (Stage 3b)** — DONE 2026-06-13 →
      `docs/aoc-rpc-method-opcodes.md` (full tables), `docs/aoc-opcode-ids.md` (Stage 3b summary),
      `Captures/ghidra/` (recovery scripts + outputs). `wire = 0x2000 + registration index`, base 0x2000 for
      every interface except PlayerAgent/PlayerInterface (hash dispatch — still open). c2s order == the catalog
      (proven from the proxy constructors); the s2c "drift" is RESOLVED by the dispatcher switches — the live
      build inserts mostly-no-arg dispatch methods the string-extracted catalog missed (true sizes: GameChar
      Interface 181/+24, CSPlayerInterface 211/+33, GameInterface 16/+3, Tradepost 8/+1). In-client anchors all
      match (DispatchUpdateCash 0x2005, DispatchShopData 0x2033, DispatchAddSpecialAbility 0x2052, ...).
      `Opcodes.cs` GameServerRespondOpcodes annotated with true identities; TradepostProtocol.InterfaceMethod
      corrected to the real 8-method table. Remaining: PlayerInterface hash fn; wiring specific s2c opcodes into
      handlers with (class id) capture correlation; naming the no-arg insertions.

First pass IMPLEMENTED 2026-06-12 (same day); what remains is the deeper model work:

- [x] **`RespawnMe` 0x2024** — respawn is now CLICK-driven (`WorldSimulation.TryRespawnNow`; the 10s tick timer
      stays as the fallback). Retail reply sequence decoded for later fidelity: vitals → `TombStoneFullUpdateIIR`
      (tombstone at the death spot, NOT sent yet) → teleport + re-render at the resurrection point (= our
      existing flow).
- [x] **`RequestBindLocation` 0x2002 (x49)** — answered with `0x2002 [u32 playfieldId]` (verified 1:1 across 13
      captures). It is the **Totem of Origin home playfield**; we reply with the character's current map.
      - [ ] Real bind model: persist a bind location (inn/wayfarer binding) + the totem teleport itself
            (`use_totem_of_origin` capture exists).
- [x] **`TryLootCash` 0x2045** — consumed gracefully (no more unknown-packet noise); the actual coin grant is
      blocked on the money model (§9c) — Tortage starter bags carry no cash, so no retail reply to mimic yet.
- [x] **`CancelBuff` 0x201E** — full buff model (2026-06-12): `WorldEntity.ActiveBuffs` registry, self/no-target
      casts register, CancelBuff removes (retail sends no removal IIR — just a FormatFeedback text; the client
      clears its own icon), tick expires entries with durations, `.buffs` GM command.
      - [ ] Durations + stat effects need the RDB spell definitions (§5); all entries are duration-less until then.
- [ ] **"Shield of the Risen" should auto-apply on login / playfield change** (Melvin, 2026-06-13): this buff
      (a starting/racial shield?) is present in retail on world entry; apply it via the buff model
      (`ApplySpell`/`ActiveBuffs`) at login AND on every playfield/map change, the same hook the starting
      abilities use (`GrantStartingAbilities`). Need the buff/spell id (find it in a login capture's ApplySpell /
      the snapshot's active-buff list).
      - [ ] Mount buff and other direct ApplySpell sends bypass the registry (only the Handle0x2000 cast path
            registers).
- [x] **Hotbar persistence (server side)** — `CharacterShortcuts` table; `AddShortcut` upserts,
      `RemoveShortcut` deletes (migration `AddCharacterShortcuts`).
      - [ ] The login RESTORE is undecoded: `RequestShortcutBar` gets no direct retail reply — the bar most
            likely rides inside the FullChar 0x737A6DF9 snapshot (RE-gated). `UpdateShortcut` 0x206F
            ([slot][a][b]) semantics also undecoded; logged, not persisted.
- [ ] `ScryMessageBoxAnswer` 0x203B (x1), `RequestMassivePvPScoreData` 0x208D (x1) — note only.
- Suspicious historical behavior kept until a live check: we answer every `SetSprintInputState` 0x200C
  (x2092 — the sprint key) with a `ReportDimensionId`; retail does not.

---

## 5. Content from the client RDB

Item templates + dialogue text are cracked (TEA-ECB, memory `rdb_extraction`); the strategy shift from
"scrape captures" to "import real client data" is unblocked for the remaining gaps. Full plan + index format:
`docs/archive/rdb-extraction-plan.md`.

- [ ] NPC / creature definitions (real max HP, replacing the spawn-seeded value).
- [x] **Starting abilities — extracted class-aware from the capture 0x2052 grants (2026-06-13).** The archive has
      no starting kit, so `CaptureAbilityImporter` now reads the `0x2052` ability grants from the create-flow
      captures and attributes each to that capture's class (parsed from its `ChangeClass` command, value/2). An
      ability granted across ≥2 classes is GENERIC (`Ability.Class=0`, e.g. the 3 Normal Attacks + toggle-combat /
      switch-weapon); single-class ones are class-specific (beach data: 269272 → Guardian). `GetStartingAbilities(level,
      class)` grants generic + the character's class set; in-client a created char now has its attacks. (Fix that
      unblocked it: `TsharkCaptureReader` was discarding the real opcode — every rebuilt packet read as 0x2000 — so
      the importer saw zero grants.) The **class-id mapping was NOT a bug** — the enum is correct (Barbarian 0x12,
      Necromancer 0x29, verified against `docs/guilds.md`); Testertwo was simply stored as Necromancer from its
      import and was corrected in the DB. Class-specific coverage is bounded by the captured classes (incomplete is
      acceptable). Could still extend to a per-(class,ability) many-to-many if a subset-shared ability ever appears.
- [ ] Spell / ability definitions (names + params) — also unblocks real spell-grant (no capture has it; needs a
      fresh caster capture OR RDB import). → `docs/archive/todo-history.md` §3 "spells blocked on data"
      NOTE (2026-06-13): a LEVELED char's full spell book is NOT sent as discrete grants — captures have zero
      `0x200A` DispatchLearnSpell and only the standard ~24 `0x2052` combat grants (even lvl-80 necrol80). The spell
      book is baked into the `0x737A6DF9` FullChar snapshot, which balloons with level (~10 KB fresh beach char →
      **~620 KB for lvl-80 necrol80** in `account-faolan-character-necrol80-login-mount_move_dismount`). So the
      in-capture source of a leveled char's real spell identities is that snapshot blob — decoding its spell
      section is the purist path (dense RE); the wiki scrape above is the pragmatic one for names/levels.
- [ ] **IDEA — scrape the AoC fandom wiki for catalog data (names + levels).** `https://aoc.fandom.com/wiki`
      (`/wiki/Spells` has spell names + levels) is a ready-made source for the gaps captures can't fill: spell
      names/levels and (likely) item level-requirements (the `Items.Level`-is-0 gap that blocked level-appropriate
      gear, `protocol_midsession_spawn_blocked`'s sibling issue). Captures only ever show a handful of a char's
      abilities — even a lvl-80 endgame capture (necrol80) yielded just 24 grants, all the standard combat set — so
      the full spell catalog won't come from captures. Write a scraper → import names/levels into Spells/Items as a
      supplement to RDB extraction. (Respect the wiki's license/attribution; cache scraped pages.)
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

- [ ] **Remove the C# packet analyzer — tshark is the source of truth now** (Melvin, 2026-06-13). The
      `Faolan.PacketAnalyzer` analyzer drops ~2/3 of compressed downloads (incl. ALL conversation responses), so
      it is NOT trustworthy; the Faolan-Wireshark plugin + `Captures/tshark_dump.sh` (TCP-reassembled
      `*.tshark.json`) is now the canonical capture format and the importer reads it (`TsharkCaptureReader`).
      Once every capture is re-dumped as `*.tshark.json` (Melvin is doing this + deleting the old analyzer
      `.json`): delete the `Faolan.PacketAnalyzer` project + `PacketAnalysisService`, and drop the analyzer-JSON
      (`ReadJsonCapture`, `RawBytesBase64`) + pcap (`ReadPcapCapture`) paths from `Faolan.Importer/Program.cs`,
      leaving only the tshark reader. Check no other consumer depends on the analyzer (the `--dump` CLI, tests
      that glob `*.json` for `RawBytesBase64`). Then drop the dotnet-ef-introduced `dotnet-tools.json` only if
      unwanted (it pins the EF migration tool).

- [ ] **HIGH VALUE — embedded capture data out of source code.** `CreateFlow/` is ~8.8k lines, 80%+ raw hex
      byte-literals (`RetailCreateWorldEntryBurst.cs` alone = 6,957 lines). Move it to a DB table + importer
      (mirror `MapWorldBurst` / `IirRecordCodec`) with a **byte-equality test (DB-loaded == embedded arrays)** as
      the safety net, then delete the embedded file. Catch: the burst weaves the live instance id into ~324
      packets via `AppendVarint` — needs a template-with-substitution capture, not a flat blob. Verifiable
      without the client (the byte-equality oracle).
- [ ] Fold `Packets.cs` sender-bytes duplication into `World/WorldFraming` (verify which conversation senders are
      truly identical before merging). Small, safe.
- [ ] Rename `Packets/FullCharSnapshot/BigboyTailsFile.cs` to something descriptive. Trivial.
- [ ] **GameServer dispatch hardening (not a blanket rewrite).** The GameServer multiplexes ~4 RPC class-pairs
      over one connection; the `0x20xx` opcode is a per-class method selector that COLLIDES across classes, so the
      correct dispatch key is `(endpoint class id, method opcode)`. Decided AGAINST rewriting the whole opcode
      switch to class-first (it's in-client-verified, and class-first can't disambiguate the shared
      `GameCharAgent`/`GameInterface` classes anyway). Instead, surgically: (a) route dedicated-class subprotocols
      class-first (Tradepost done via `TradepostProtocol.IsInbound`); (b) for shared-class methods (e.g. vendor
      `0x2034/0x2035` on `GameCharAgent`), add a one-line `Sender.Type ==` guard on any opcode case that collides
      with a handled opcode; (c) **cheap win — log unknown `(class, method)` combos in the `default` branch**
      instead of silently dropping them, so future collisions/new subprotocols surface as log lines. See memory
      `protocol_gameserver_rpc_multiplexing`.

---

## 8. Lower-priority / future

- [ ] **Persist chat history to the DB** (Melvin, 2026-06-13): chat is currently fire-and-forget (the Claude
      channel log is in-memory, capped at 500, lost on restart; channel chat / says / whispers aren't recorded
      at all). Add a ChatLog table (timestamp, kind: channel/say/whisper/system, channel id, sender id+name,
      recipient for whispers, text) appended from the AgentServer paths, so history survives restarts and the
      Claude bridge can rewind from the DB instead of skipping backlog.
- [ ] **`.<command>` should work in ANY chat channel, just like `say`** (Melvin, 2026-06-13): a `.foo` typed in
      any channel (not just the local say channel) should run as a GM command — same `.`-prefix / account >=
      GameMaster gate as the say path (`GameServerListener.cs:704` -> `HandleGmCommand`). **And do NOT echo the
      command line to chat** when it's recognized as a command (today a `.cmd` in a channel broadcasts the literal
      text). Wire the `.`-prefix GM dispatch into every channel-chat handler (`AgentServerListener` ChannelChat),
      and suppress the relay/echo for command lines. Subsumes the Claude-channel-commands item below.
- [ ] **Claude channel: reply in the SAME mode you were addressed in** (Melvin, 2026-06-13): when a player talks
      to Claude on the Claude CHANNEL, Claude should reply on the channel (broadcast); when WHISPERED, reply as a
      whisper. Today the faolan-chat reply tool defaults to whisper unless told otherwise — make the bridge pass
      the input mode (channel vs whisper) through so the reply matches it automatically.
- [ ] **Run `.commands` posted in the Claude channel as GM commands** (Melvin, 2026-06-13): a `.foo` message
      on the Claude channel should execute as a GM command (like normal chat), not just bridge to Claude.
      Normal chat already does this at `GameServerListener.cs:704` (`.` prefix, not `..`, account >= GameMaster
      -> `HandleGmCommand`). The seam to reuse exists: `IGameServerDispatcher.RunGmCommand(characterId, text)`
      (GameServerProvider.cs:25) resolves the player's GameServer session and invokes `HandleGmCommand`. Wire it
      into the `ChannelChat` handler (`AgentServerListener.cs:143`): when `channelId == ClaudeChannel.ChannelId`
      and the message is a `.command`, gate on `client.Session.Account.Type >= GameMaster` and call
      `RunGmCommand(client.Session.Character.Id, message)` instead of (or before) the `_claudeChannel.Append`
      bridge. Decide whether GM-command lines should still echo into the bridge log (probably skip, to avoid
      Claude "replying" to a command).
- [ ] **Add Claude to the friends list** (Melvin, 2026-06-13): see whether the synthetic Claude identity
      (ClaudeChannel.SenderId 0xC1A0DE05, the 0x0014-announced "Claude" sender) can appear on a player's
      in-game friends list - needs the friends-list protocol decoded first (add/remove/status packets are
      uncaptured; capture a retail friend-add to start). Whispers to Claude already route to the MCP bridge,
      so a friends entry would mainly give presence/easy access.


- [x] **Expand the tshark/Wireshark plugin to fully decode packets (headers)** — DONE (2026-06-12):
      `Faolan.Wireshark` now dissects the full Conan protocol: per-direction packet framing with in-band
      compression transitions, RpcHeader ProtoEndpoints (class ids resolved to the 14 recovered class
      names), raw opcodes (names deliberately not embedded), CRC verification, retransmission/out-of-order
      handling, and automatic following of the service-handoff addresses (a session decodes from just port
      7010). The AgentServer chat protocol ([u16 opcode][u16 bodyLen] framing) is auto-detected per
      conversation and dissected too. Validated byte-exact (per-opcode counts, Conan + chat) against the
      analyzer JSON on 4 reference captures.
      Use `-o tcp.analyze_sequence_numbers:FALSE` on lossy captures. → `Faolan.Wireshark/README.md`

- [ ] **`tshark_dump.sh` jq step is slow** (Melvin, 2026-06-13): the dump pipes `tshark -T json` through
      `jq -f tshark_to_dump.jq`, and the jq pass is the bottleneck (slow on larger captures). Now that tshark is
      the canonical capture format and every capture is being re-dumped through it, this is on the hot path. Look
      into: replacing jq with `tshark -T ek`/`-T fields` (faster, line-delimited) + a small reshaper, or emitting
      the dump JSON directly from the Faolan-Wireshark plugin (a tap/postdissector) instead of post-processing
      tshark's verbose `-T json`, or streaming jq (`--stream`) to avoid loading the whole tree.

- [ ] **Wireshark plugin: recover from capture holes (lost segments)** — a single lost inbound TCP segment
      poisons the zlib stream and the dissector marks every later download "compression state: failed"
      (whole combat phase of `account-stricken998-character-combat_armed-and-unarmed.pcapng` undissected;
      lost segments at frames 104/119/138/171/180/228...). The stream has NO zlib sync-flush points
      (0 `00 00 FF FF` markers), but the 9-byte `80 00 00 05` transition headers re-init the compression
      state at packet boundaries — on inflate failure, scan forward to the next transition marker and
      resume from its fresh zlib stream instead of failing terminally. Mark the gap with an expert info.

- [ ] **Wireshark plugin: CSPlayerAgent (port 7001) RpcHeader mis-framed** (found 2026-06-13 chasing the chat
      bug) — the plugin dissects our CSPlayerAgent server→client responses with the WRONG opcode. In
      `Captures/loopback2.pcapng` frames 484/486/488 (src=7001) are our real `Authenticate` ack / `0x20D0` /
      `0x20C5` responses (bodies `00000001`, `000003f1`, the `GroupFinder…` remoteformat — all correct), but the
      plugin reports `op=0x0a0a`, `SenderClass=None`, and the REAL opcode + tail spill into `HeaderExtraHex`
      (`0dc860d5bb10d5cb8b40`**`2000`** / `…`**`20d001`** / `…`**`20c501`**). So it over-/under-reads the RpcHeader
      and then reads the opcode (and direction) from the wrong offset. The same family on RETAIL (port **7012** in
      retail's layout — chat=7001/csplayer=7012 is swapped vs our chat=7012/csplayer=7001) dissects correctly as
      `0x2000`. Likely cause: the dissector keys the header layout / service / direction off the retail PORT
      numbers (or off the auto-followed handoff that assumed retail ports) instead of parsing the ProtoEndpoint
      framing length-by-length; it miscounts when the response RECEIVER endpoint carries a multi-byte instance
      varint (our reply swaps sender/receiver via `WriteHeader(packet.ReceiverBytes, packet.SenderBytes, …)`, so
      the receiver = the client's `{0xBBD560C8, instance=0x0802E5D5}` with a 4-byte varint). Fix: derive
      sender/receiver field lengths from the protobuf framing itself and assign service+direction from the
      followed handoff's service discriminator (`fae5989d02`=AgentServer, `e0d4b4d705`=CsPlayerAgent,
      `f9d8d9c50d`=GameServer), never from hardcoded retail ports. NOTE: this is a DISSECTOR display bug only —
      the live client parses our CSPlayerAgent responses fine (it connects to 7001 and proceeds); it is NOT the
      cause of the chat (7012) connection never opening (see §2). → `Faolan.Wireshark/`

- [ ] **ConanMessageFramer: handle mid-chunk compression transitions** — found while building the
      Wireshark dissector: the 9-byte `80 00 00 05` transition header occurs at PACKET boundaries (not
      only stream start), and its last byte is a compression FLAG (`00` = zlib follows, `01` = stream
      stays plaintext; the retail client sends a flag-`01` one mid-stream on the GameServer connection).
      `ConanMessageFramer.PrepareBytes` only checks receive-chunk starts and always assumes compression,
      so a marker arriving mid-chunk silently discards the rest of that chunk (drops one client ping per
      session in the reference captures; harmless today, but a correctness gap for the c2s path).

- [ ] **Real password validation** — login is DH + TEA-CBC; we accept any password (validation stubbed). Needs a
      client DH-key binary patch + C# port of the cipher. Fully understood, not implemented. → `docs/archive/login-decryption-plan.md`
- [x] **Recover real opcode / RPC names** — DONE: IIR sub-opcodes = BKDR-31 name hashes
      (`docs/aoc-opcode-ids.md`); 16-bit RPC opcodes = `0x2000 + registration index` in BOTH directions
      (`docs/aoc-rpc-method-opcodes.md`, scripts `Captures/ghidra/`). c2s catalogs in `Opcodes.cs`; the
      server→client tables (once thought to "drift") are recovered from the per-class dispatcher switches — the
      drift was just extra no-arg dispatch methods the string catalog missed. Only PlayerInterface/PlayerAgent
      (32-bit hash dispatch, login) stays behavior-derived. → README "Client reverse-engineering"

---

## 9. Player economy — auction house, then vendor  ⭐ NEXT FEATURE (queued 2026-06-12)

Two separate systems; do them in order. Both are feasible now — we have RE'd RPC surfaces + captures + real DB
item identity + the proven analyzer→decode→construct-from-DB toolchain. Neither is blocked on missing data.
Captures (cover both): `Captures/account-faolan-character-necrol80-login-sell_items_to_trader-intract-with-auctionhouse`
and `...-assassinl23-login-buy_from_autionhouse_get_mail-...` (~3.8k / ~2.8k packets).

### 9a. Auction house = "Tradepost" + mail (FIRST)
**Mail is a hard dependency, not a bonus:** a bought item is delivered to the buyer **via in-game mail**, and
the mail system lives on the *same* `TradepostAgent` — so build mail alongside the AH (or first). Mail API on
the agent: `SendMail(recpName, body, money, bankItemIndices)`, `GetAllMails`, `GetMailByID`, `GetMailItems`,
`MarkMailRead`, `DeleteMail`; server→client `DispatchMailMessage`, `DispatchSendMailResult`,
`DispatchRetrieveMailResult`. Needs a `Mail` + `MailItem`/`MailAttachment` DB model (sender, recipient, body,
attached money + items, read flag) and item escrow on send → grant on retrieve. This is exactly the AH
buy-delivery path (seller's item + buyer's payment move through mail).
- `TradepostAgent` (class **29**, opcode `0x3D744981`) — client→server: `TradepostOpen`, `TradepostItemSearch`,
  `TradepostSellItem(nBankItemSlot, nPrice)`, `TradepostBuyItem`, `TradepostChangeItemPrice`,
  `TradepostCancelSellItem` + full mail API (`SendMail`/`GetAllMails`/…).
- `TradepostInterface` (class **56**, opcode `0xF2C2D5B5`) — server→client: `DispatchOpenTradepost`,
  `DispatchTradepostSearchResults`, `DispatchMailMessage`, …
- [x] **Step 1 (gate, client-free) — DONE 2026-06-12.** Decoded both captures (`Captures/decode_tradepost.py`).
      Full grammar in **`docs/tradepost-protocol.md`**: the generic RPC envelope (receiver/sender ProtoEndpoints +
      `0x20xx` method), the method map (Open/Search/Buy/Close + mail get/dispatch), the `TradepostItemSearch`
      query (byte-exact), the `DispatchTradepostSearchResults` rows (saleId/seller/price/qty/itemId), and the
      **buy→mail** delivery: `TradepostBuyItem(saleId)` → `DispatchMailMessage` (recipient=buyer, sender=EMPTY
      system mail, body = remoteformat `TradePostGUI_MailItemBought` with the item attachment).
- [x] **Step 2 (grammar) — DONE 2026-06-12.** `0x2005` rows validated byte-exact (`[head:u32][row]*`, row =
      saleId/handle/seller/price/qty/itemId + a self-delimited `ProtoAcgItem`; sorted by price ascending). Mail
      attachment isolated (`GetMailItem` → 629→754 B; `[01][itemInstanceId:u64][protoLen:u32][ProtoAcgItem]`).
      **Both reuse the same `ProtoAcgItem` our inventory/spawn builders already emit.** `docs/tradepost-protocol.md`.
- [~] **Step 2b (in progress):** server wiring.
  - [x] Dispatch routing — tradepost packets are routed by RPC class id (`TradepostProtocol.IsInbound`, since the
        0x20xx methods collide with ordinary opcodes) to `Faolan/GameServer/Tradepost/TradepostHandler`, which
        decodes + LOGS each method (Open/Search w/ query/Buy/Close/mail). Next live AH interaction confirms our
        captured grammar on real traffic. Builds clean, additive (early-return guard).
  - [x] Reply to `TradepostOpen` with `DispatchOpenTradepost` (access pos + 15.0 radius) — `TradepostResponses`,
        echoes the client's TradepostInterface instance. **Byte-exact golden test vs the captured packet passes**
        (`TradepostResponseTests`). Still needs an in-client render check (user side).
  - [x] Reply to `TradepostItemSearch` with `DispatchTradepostSearchResults` — PoC replays a captured single-row
        listing (seller "Jocko", item 0x755D). **Byte-exact golden test passes** (`TradepostResponseTests`). Still
        needs an in-client render check (user side); Step 3 swaps the replay for DB-built rows matching the query.
  - [x] **Open trigger fixed.** The capture shows `DispatchOpenTradepost` precedes `TradepostOpen` — it's
        triggered by interacting with the auctioneer NPC, not by `TradepostOpen` (replying to Open is circular).
        PoC bootstrap = the **`.ah` GM command**, which sends `DispatchOpenTradepost` at the player's live position
        (so the within-15-radius check passes). 277 tests green.
  - [x] **`.ah` opens the window IN-CLIENT (Melvin, 2026-06-13).** Framing/endpoints confirmed live. The window's
        first/default tab is the **BANK** (personal storage) - DECODED + wired into `.ah` (memory
        `protocol_bank_storage`, `docs`-less): container kind 105 delivered via a **BankIIR (0x0BA1C1CB)** on the
        world conversation (separate channel from the tradepost AH/mail tabs), byte-exact `BankStorageTests`. The
        bank tab renders and the player can move items in/out in-client.
  - [x] **AH SEARCH is now DB-backed (2026-06-13).** `TradepostItemSearch` queries the new `TradepostListing`
        table by the searched `itemId` (cheapest first) and builds `DispatchTradepostSearchResults` byte-exact via
        `TradepostListingCodec` (round-trip golden test `TradepostListingCodecTests` vs the captured "Jocko" row).
        The full row grammar was nailed: `[saleId][handle][seller][price][qty][itemId][u64][u32][entryLen]
        [ProtoInventoryGameEntry]` — the entry is the SAME message the snapshot/shop/add-item use (item_id=saleId,
        stats {0,26:qty,258:price,265:ts}, item_data=the listed instance's ProtoAcgItem). The captured single-row
        replay is the FALLBACK when no listing matches (so the grid still renders pre-import). Remaining: a
        listings importer/seed (harvest captured search rows into `TradepostListing`) so search returns real data
        without the fallback.
  - [ ] **AH "offer for sale" should work** (Melvin, 2026-06-13): the c2s list-item request → create a
        `TradepostListing` (seller/price/qty/itemId), remove the item from the bank (container 105) into escrow, and
        the listing then shows in search. The tradepost sells from the BANK (`nBankItemSlot`).
  - [ ] **Bank deposit/withdraw must PERSIST**: moving items between the bank (container 105) and the bag must be
        written to `CharacterInventory` so the bank survives relog (the client move sends go through the inventory
        move handler - route bank-slot moves to/from container 105).
- [~] **Step 3:** go DB-backed. `TradepostListing` table + DB-backed search DONE (2026-06-13, above). Still:
      `Mail`/`MailAttachment` tables, SELL into the AH (create a listing from a bank item + escrow), money debit,
      buy→mail grant. NB: tradepost sells from the **bank** (`nBankItemSlot`) — leans on bank/money support (the
      bank itself is now decoded + delivered); `CSBuyItemReply` hints at a commerce/CS path. All on the existing
      GameServer dispatcher (no new connection).

### 9c. Money storage / usage (cross-cutting prerequisite)

Core model IMPLEMENTED 2026-06-12. The wallet-change packet was decoded from the loot/sell/buy captures:
**DL `0x2005` (GameCharAgent conversation, 12 bytes) = `[u64 balanceTin][u32 reason]`** (3 = gain, 0 =
spend/sync; the value is the ABSOLUTE balance — grows +2 per beach coin loot, decreases on purchases, persists
across capture sessions). Golden-tested (`MoneyUpdateTests`).

- [x] **Storage**: `Character.Money` (tin u64 - the wire unit, 100 tin = 1 copper; migration `AddCharacterMoney`) + `SendMoneyUpdate` (0x2005).
- [x] **Loot cash**: bags roll coins at drop (`LootResolver.RollCash`, anchored on the observed 2c beach bags),
      `TryLootCash` grants once per bag, persists, pushes the balance.
- [x] `.money [<tin> | +<tin>]` GM command (in-client verified: .money 100 displays 1 copper, so the wire unit is tin).
- [x] **In-client verify** (2026-06-13): `.money 100` displays 1 copper in-client → the `0x2005` wallet HUD
      update path is confirmed live. Loot-coin grant + relog persistence ride the SAME `SendMoneyUpdate` path
      (now low-risk, not separately exercised). Open caveat unchanged: we push an interim 0x2005 sync at login;
      retail carries the initial balance in the FullChar snapshot (field undecoded) — watch for double-count.
- [x] **Vendor sell credit** (§9b sell side, 2026-06-12) — the first real money-usage path.
- [x] **Vendor BUY debit** (§9b buy side, 2026-06-13) — `BuyItem` → `GrantItemToBackpack` + a `DispatchUpdateCash`
      spend; grant aborts before any money moves on a full bag.
- [ ] **Remaining usage paths**: AH listing/buy + mail COD (§9a), guild bank
      (`GuildTransferMoney`/`GuildWithdrawMoney`; `Guild.Cash` is persisted; no captures exercise the bank UI, so
      the bodies are unknown).

### 9b. NPC vendor / shop buy-sell (AFTER 9a — simpler)
Rides on **`GameCharAgent`/`GameCharInterface`** — the main GameServer connection, already fully wired; **no new
connection needed**, and likely simpler than the AH (no listings/search/mail).
- client→server (`GameCharAgent`, wire opcodes now confirmed in captures): `CloseShop` = 0x2032 (x12),
  `BuyItem(i_cShopID, i_nPos)` = 0x2033 (x1), `SellItem(i_cShopID, i_cInventoryID, i_nPos)` = 0x2034 (x8),
  `UndoLastSoldItem` = 0x2035. No `OpenShop` exists in the recovered GameCharAgent table — the window likely
  opens via the dialogue/IIR side (`KnubotStartTradeIIR` 0x4B2A47BF, x2 in captures = the trader sessions).
- server→client (`GameCharInterface`): `DispatchShopData(shopID, parentID, shopContent)`,
  `DispatchItemSoldToShop(inventoryID, pos)`, `DispatchCloseShop`.
- [x] **Shop flow decoded + SELL side implemented (2026-06-12)**: DL `0x2033 DispatchShopData` = [shop ID32
      (type 0xC75B)][vendor NPC ID32][protobuf shop content]; `SellItem` 0x2034 = [shop ID32][u32 container]
      [u32 charId][u32 slot] (container 0x68 = the DB bag container 104, no mapping needed); reply =
      `0x2035 DispatchItemSoldToShop` [container][charId][slot] + the 0x2005 wallet credit. Sell handler removes
      the `CharacterInventory` row, credits `Character.Money`, supports `UndoLastSoldItem` (single-step restore).
      Golden tests `ShopTests`.
- [ ] **Wire the real shop-OPEN trigger**: an unsolicited `DispatchShopData` doesn't POP the window. The vendor
      window is gated on the client's own NPC trade-initiation (KnubotStartTrade), which establishes the
      conversation retail's `DispatchShopData` rides; the reply must echo the inbound conversation (like the working
      `SellItem`→`0x2035` path does). `SendShopData` (2026-06-13) serves the DB content + records the open-shop
      session state, exposed via the `.shop [vendor]` GM bootstrap; it makes the BUY round-trip exercisable but
      doesn't pop the window — wiring KnubotStartTrade is the remaining piece.
- [x] **Shop-content protobuf DECODED + DB-backed shops (2026-06-12)**: the content is a plain
      `ProtoInventoryGame` (the bag message!) with container type 111; each ware is a standard
      `InventoryListEntry` whose ProtoAcgItem tail carries the FULL item identity (Data1-4 quad + template +
      archetype). `ShopContentCodec` parses + builds **byte-exact** (round-trip golden test vs the captured
      trader blob). Importer: `CaptureShopImporter` (6 vendors, keyed by NPC name) + shop wares as
      `ArchetypeHarvester`'s third source (+281 items, +324 buildable). `ShopContentCodec.Build` CONSTRUCTS the
      window byte-exact from `Shop`/`ShopItem`/`Items` rows, ready for whenever the open-trigger above is wired.
- [x] **BUY side implemented (2026-06-13)**: `BuyItem` (c2s 0x2033 = [shop ID32 type][shop instance][slot])
      resolves the slot against the session's open shop (`_openShop`, recorded by `SendShopData`), debits the
      wallet (`DispatchUpdateCash` spend) and grants the ware live (`DispatchAddItem` via `GrantItemToBackpack`).
      The grant runs first so a full bag aborts the purchase before any money moves. `ShopTests` cover the buy
      pricing invariants. Still client-gated on the shop-OPEN trigger above (the window doesn't pop yet).
- [ ] **Real prices**: the RDB item templates carry item value (TEA key cracked, field not mapped); provisional
      sell = level×2×count (`VendorPriceTin`), buy = TailLevel×2×count×4 (`VendorBuyPriceTin`, a markup so wares
      can't be flipped for free). Captured sell anchors: 19/23/92/392 tin. Stat-356 semantics still unknown.

---

## Known non-blockers (no action unless curious)

- `OverrideGameTweaks` (`0x2085...`) — client logs `stream bad after 'overrides'` and drops that one route every
  session; harmless, pre-existing.
- Analyzer tail-drop on ~3 GameServer streams (a few KB short vs ground truth); doesn't affect gameplay.
  → memory `analyzer_tcp_reassembly_gap`
