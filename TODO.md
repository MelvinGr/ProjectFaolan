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

### 4b. Named-but-unhandled client packets (surfaced by the opcode mapping, 2026-06-12)

Observed in captures (counts in parens), now identified via the method-index tables, currently falling into the
unknown-packet log. Each is a concrete gameplay gap with a known entry point:

- [ ] **`TryLootCash` 0x2045 (x23 — every loot!)** — looting coins is never granted; pairs 1:1 with the handled
      `TryLootAll` 0x2043. The missing half of the live loot item-grant (§2 inventory/loot).
- [ ] **`RespawnMe` 0x2024 (x2)** — retail respawn is CLICK-driven (the "resurrect" button), ours is a tick
      timer that auto-respawns. Handle the click; keep the timer as fallback. Related: **`RequestBindLocation`
      0x2002 (x49)** — the client repeatedly asks for its bind/resurrection point and we never answer.
- [ ] **`CancelBuff` 0x201E (x1)** — right-click-cancel a buff; ignored.
- [ ] **`RequestShortcutBar` 0x206D (handled = logged only) + `UpdateShortcut` 0x206F (x3) + the handled
      `AddShortcut`/`RemoveShortcut`/`MoveShortcut`** — the full hotbar-persistence protocol is now named;
      persistence itself unimplemented.
- [ ] `ScryMessageBoxAnswer` 0x203B (x1), `RequestMassivePvPScoreData` 0x208D (x1) — note only.
- Suspicious historical behavior kept until a live check: we answer every `SetSprintInputState` 0x200C
  (x2092 — the sprint key) with a `ReportDimensionId`; retail does not.

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

- [ ] **Real password validation** — login is DH + TEA-CBC; we accept any password (validation stubbed). Needs a
      client DH-key binary patch + C# port of the cipher. Fully understood, not implemented. → `docs/archive/login-decryption-plan.md`
- [x] **Recover real opcode / RPC names** — DONE for the meaningful parts (2026-06-12): IIR sub-opcodes = BKDR-31
      name hashes (`docs/aoc-opcode-ids.md`), and the 16-bit RPC opcodes = method index (0x2000 + position) into
      the recovered per-class client→server tables — full catalogs in `Opcodes.cs`. Remaining gap: the
      server→client interface tables drift (live build has unnamed extra methods) so downloads stay
      content-named; closing that would need Ghidra on the dispatch tables. → README "Client reverse-engineering"

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
  - [ ] **In-client check (user side):** stand somewhere, type `.ah` → does the auction-house window open? Then
        search → does the listing grid show the replayed row ("Jocko", item 0x755D)? Grab the client log on
        failure (`stream bad`, `invalid ...`, `ResourceManager ... does not exist`). Candidates if it doesn't
        open: the `BankIIR 0x0BA1C1CB` that accompanies `DispatchOpenTradepost`, or the interface instance
        (0x08006F31) not matching this client. If it renders, framing/endpoints are confirmed live → Step 3.
- [ ] **Step 3:** go DB-backed — `TradepostListing` + `Mail`/`MailAttachment` tables, search, money/escrow,
      buy→mail grant. NB: tradepost sells from the **bank** (`nBankItemSlot`) — leans on bank/money support;
      `CSBuyItemReply` hints at a commerce/CS path. All on the existing GameServer dispatcher (no new connection).

### 9b. NPC vendor / shop buy-sell (AFTER 9a — simpler)
Rides on **`GameCharAgent`/`GameCharInterface`** — the main GameServer connection, already fully wired; **no new
connection needed**, and likely simpler than the AH (no listings/search/mail).
- client→server (`GameCharAgent`, wire opcodes now confirmed in captures): `CloseShop` = 0x2032 (x12),
  `BuyItem(i_cShopID, i_nPos)` = 0x2033 (x1), `SellItem(i_cShopID, i_cInventoryID, i_nPos)` = 0x2034 (x8),
  `UndoLastSoldItem` = 0x2035. No `OpenShop` exists in the recovered GameCharAgent table — the window likely
  opens via the dialogue/IIR side (`KnubotStartTradeIIR` 0x4B2A47BF, x2 in captures = the trader sessions).
- server→client (`GameCharInterface`): `DispatchShopData(shopID, parentID, shopContent)`,
  `DispatchItemSoldToShop(inventoryID, pos)`, `DispatchCloseShop`.
- [ ] Decode the shop flow from the `sell_items_to_trader` capture (`OpenShop`→`DispatchShopData` shop inventory
      → `Buy/SellItem` → inventory update), then build a DB-backed vendor (shop-content table per NPC + price +
      money debit/credit on buy/sell).

---

## Known non-blockers (no action unless curious)

- `OverrideGameTweaks` (`0x2085...`) — client logs `stream bad after 'overrides'` and drops that one route every
  session; harmless, pre-existing.
- Analyzer tail-drop on ~3 GameServer streams (a few KB short vs ground truth); doesn't affect gameplay.
  → memory `analyzer_tcp_reassembly_gap`
