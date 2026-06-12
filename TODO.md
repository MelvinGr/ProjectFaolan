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

### 9c. Money storage / usage (cross-cutting prerequisite)

Core model IMPLEMENTED 2026-06-12. The wallet-change packet was decoded from the loot/sell/buy captures:
**DL `0x2005` (GameCharAgent conversation, 12 bytes) = `[u64 balanceCopper][u32 reason]`** (3 = gain, 0 =
spend/sync; the value is the ABSOLUTE balance — grows +2 per beach coin loot, decreases on purchases, persists
across capture sessions). Golden-tested (`MoneyUpdateTests`).

- [x] **Storage**: `Character.Money` (copper u64, migration `AddCharacterMoney`) + `SendMoneyUpdate` (0x2005).
- [x] **Loot cash**: bags roll coins at drop (`LootResolver.RollCash`, anchored on the observed 2c beach bags),
      `TryLootCash` grants once per bag, persists, pushes the balance.
- [x] `.money [<copper> | +<copper>]` GM command.
- [ ] **In-client verify**: loot coins → wallet HUD updates; balance survives relog (we push an interim 0x2005
      sync at login — retail carries the initial balance in the FullChar snapshot instead, field undecoded; if
      the client ignores or double-counts the login push, find the snapshot field).
- [x] **Vendor sell credit** (§9b sell side, 2026-06-12) — the first real money-usage path.
- [ ] **Remaining usage paths**: vendor BUY debit (item-grant-gated, §9b), AH listing/buy + mail COD (§9a),
      guild bank (`GuildTransferMoney`/`GuildWithdrawMoney`; `Guild.Cash` is persisted; no captures exercise
      the bank UI, so the bodies are unknown).

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
      `.shop` GM command opens the captured trader window (PoC blob replay, like `.ah`). Golden tests `ShopTests`.
- [ ] **In-client verify `.shop`**: window opens; selling a bag item clears the slot + wallet updates; undo
      restores. (The blob's embedded vendor handle is session-volatile — expected to open anyway, like `.ah`.)
- [x] **Shop-content protobuf DECODED + DB-backed shops (2026-06-12)**: the content is a plain
      `ProtoInventoryGame` (the bag message!) with container type 111; each ware is a standard
      `InventoryListEntry` whose ProtoAcgItem tail carries the FULL item identity (Data1-4 quad + template +
      archetype). `ShopContentCodec` parses + builds **byte-exact** (round-trip golden test vs the captured
      trader blob). Importer: `CaptureShopImporter` (6 vendors, keyed by NPC name) + shop wares as
      `ArchetypeHarvester`'s third source (+281 items, +324 buildable). `.shop [vendor]` now CONSTRUCTS the
      window from `Shop`/`ShopItem`/`Items` rows (blob fallback only when no shops imported).
- [ ] **BUY side**: positions now resolve to items via `ShopItem`; still blocked on the live inventory-add
      path (the same item-grant wall as loot, `protocol_containeradditem`) and on prices (stat 356 semantics
      unknown - not obviously a price; the client may price from the RDB item template).
- [ ] **Real sell prices**: the RDB item templates carry item value (TEA key cracked, field not mapped);
      provisional price = level×2×count (`VendorPriceCopper`). Captured anchors: 19/23/92/392 copper.

---

## Known non-blockers (no action unless curious)

- `OverrideGameTweaks` (`0x2085...`) — client logs `stream bad after 'overrides'` and drops that one route every
  session; harmless, pre-existing.
- Analyzer tail-drop on ~3 GameServer streams (a few KB short vs ground truth); doesn't affect gameplay.
  → memory `analyzer_tcp_reassembly_gap`
