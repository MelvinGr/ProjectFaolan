# Autonomous overnight session — handoff

**You are working unattended.** Melvin is asleep and will review in the morning. Your job: work through
`TODO.md` and complete as much as you safely can **without him**, committing each verified milestone. Do not
wait for input, do not ask questions — if an item needs Melvin, the retail client, or a fresh capture,
**skip it and move to the next**. Read `CLAUDE.md` first (it has the commands + conventions).

**Ghidra / client RE is fair game.** The headless Ghidra project is set up at `/tmp/ghidra-aoc/proj`
(rebuildable; the exe is stripped, no PDB) with scripts in `Captures/ghidra/` and `/tmp/ghidra-aoc/scripts/`.
This is how the s2c dispatch tables, the BE32 snapshot framing, and the launch token were recovered —
methodology in `docs/archive/db-container-handoff.md` ("Ghidra notes"): anchor on error-string literals,
pointer-search the C-string's LE32, decompile the (de)serializers. **Crucially, Ghidra can often recover a
wire layout that no capture exercises** — read the client's own serialize/deserialize code instead of guessing.
So an item flagged "needs a fresh capture" below may actually be Ghidra-recoverable; prefer RE over guessing.
Commit RE findings as `docs/` writeups + the recovery scripts; any builder that results still needs its
byte-exact test.

## Ground rules (non-negotiable)
1. **Byte-exact or it doesn't ship.** Every new wire builder gets a golden test in `Faolan.Tests` proving it
   matches a capture (or a round-trip), per the construct-don't-replay loop. No speculative packets.
2. **Green tree at every commit.** Run `dotnet build ProjectFaolan.slnx` and `dotnet test Faolan.Tests`
   before each commit; never commit a red build/test. Commit one verified milestone at a time
   (conventional-commit style), `faolan.db` rides along when content changed.
3. **Stay on the `development` branch. Commit locally only — do NOT push, open PRs, or touch `main`.**
4. **Don't refactor away magic constants / verbatim payloads** unless the replacement is proven byte-for-byte
   against a capture. Wrong-looking values are usually capture-derived — check the comment first.
5. **Keep the trackers honest.** When a subsystem's status changes, update `TODO.md` (check the box, note the
   commit), the relevant `docs/` file, and any affected memory. `TODO.md` is the single tracker.
6. If a server run dirties `faolan.db`, `git checkout -- faolan.db` after (unless the change was intended and
   committed). EF migrations: use the absolute-connection form in `CLAUDE.md`, never the bare `database update`.
7. **Append a line to the "Night log" at the bottom of this file after each milestone** so the morning review
   is a quick scan. Leave the tree building + all tests green when you stop.

## Starting state (2026-06-13 night)
- Branch `development`, clean, **304 tests green**. Last work: DB-backed AH search (`TradepostListingCodec` +
  `TradepostListing` table), then a docs sweep (README is now a regular readme; resolved handoffs archived;
  open work consolidated into `TODO.md`).

## Best autonomous targets (high confidence, no Melvin/client/new-capture needed) — do these first
Work them roughly top-to-bottom; each is decoded already or pure code.

1. **§9a — finish the auction-house economy thread** (continues tonight's work; the protocol is decoded
   byte-exact in `docs/tradepost-protocol.md`, captures exist):
   - **AH listings importer/seed**: harvest the captured `DispatchTradepostSearchResults` rows into the new
     `TradepostListing` table (mirror `CaptureShopImporter`), so search returns real data instead of the
     single-row fallback. Validate with a round-trip test.
   - **`Mail` + `MailAttachment` DB model** + the **`DispatchMailMessage`** builder (base + the appended
     attachment) — fully specified in `docs/tradepost-protocol.md` ("Buy → mail delivery"); golden-test the
     builder against the captured 629 B (no attachment) and 754 B (with attachment) bodies.
   - **SELL into the AH** (`TradepostSellItem(nBankItemSlot, nPrice)`): create a `TradepostListing` from a bank
     (container 105) item + escrow it; it then appears in search.
   - **BUY → mail** (`TradepostBuyItem(saleId)`): debit the buyer, create a system mail to the buyer with the
     item escrowed; mail retrieval grants it (reuse `GrantItemToBackpack` / `DispatchAddItem`). Mail ops
     (`GetAllMails`/`GetMailByID`/`GetMailItem`) answer with `DispatchMailMessage`.
2. **§4b — resolve names in the "unknown (class, opcode)" log**: every `(receiver class, opcode)` maps to a real
   method name now (`docs/aoc-rpc-method-opcodes.md`, enums in `Opcodes.cs`). Wire that lookup into the
   `default` branches so unhandled packets log their method name, not bare hex. Pure code, low risk.
3. **§7 — safe refactors** (no behavior change, tests are the safety net):
   - Remove the dead `AdjustSnapshotPrefixRootLength` (superseded by `SetSnapshotBlobLength`); migrate
     `SnapshotPrefixLengthTests` to the `SetSnapshotBlobLength` framing first.
   - Rename `Packets/FullCharSnapshot/BigboyTailsFile.cs` to something descriptive.
   - Fold `Packets.cs` sender-bytes duplication into `World/WorldFraming` (verify senders are truly identical
     first).
4. **§8 — world/system chat via `ChatTextIIR` (0xE0B51102)**: if no capture exercises the body, recover its
   layout from the client's serializer in Ghidra rather than skipping.
5. **Ghidra / RE targets** (produce `docs/` writeups + scripts; build only what you can byte-exact-test):
   - **Player-spawn rotation encoding** (§3) — decode the rotation bytes from the spawn (de)serializer
     (quaternion vs packed Euler) so the spawn can be authored from a stored facing, not kept verbatim.
   - **Name the no-arg "insertion" dispatch methods** (§4b / `docs/aoc-rpc-method-opcodes.md`): the s2c tables
     have known opcodes with no string name — recover them from the dispatcher switches.
   - **PlayerInterface / PlayerAgent login hash fn** (§4b) — the 32-bit hash method-id dispatch (not
     `0x2000+index`); known plaintext↔hash anchors are in `docs/aoc-rpc-method-opcodes.md`. Cracking it closes
     the last opcode-recovery gap. Login already works, so this is knowledge-only (low risk).
   - **Mail / trade / quest-share wire bodies** that lack a capture (§9a / §8) — recover from the client's
     (de)serializers so the builders can be authored + golden-tested against the RE'd layout.
   - Mid-session-spawn render mechanism (§2/§3): static RE is allowed and useful, but the *validation* is
     in-client — advance the analysis, write it up, don't claim it fixed without a client check.

## Do NOT attempt tonight (gated on something you don't have)
- Anything marked **in-client gate / needs a live client** (most of §2, mounts riding, cross-player visual
  checks, the mid-session-spawn blocker §2/§3, the `.ah` window render, vendor-shop window pop). You can't run
  the retail client.
- Anything needing a **fresh capture** AND not recoverable from the client in Ghidra — i.e. content that only
  exists in traffic you don't have and isn't in the client's code (e.g. grant-actual-spells *values*, Casilda's
  create-world dialogue *text*). Wire *layouts* are usually Ghidra-recoverable (see the RE targets above); don't
  guess a format, but do RE it.
- **Validation that needs the live client** — most of §2, mounts riding, cross-player visual checks, the
  mid-session-spawn *render* confirmation, the `.ah` window pop. You can RE/build toward these, but you can't
  mark them verified.
- The **§7 "embedded capture data out of source" / `RetailCreateWorldEntryBurst` → DB** move — it weaves the
  live instance id into ~324 packets and is large/fragile; leave it for a supervised session unless you can do
  a *small* self-contained burst with a byte-equality oracle and full green tests.
- The §2 **conversation-tree DB populate** — it waits on Melvin's tshark re-dump (in progress); not yours to run.

## When you finish an item
Commit it, tick the `TODO.md` box with the commit hash, update the relevant doc/memory, append to the Night
log below, then pick the next item. If you exhaust the safe list, stop and summarize — do not start gated work.

## Night log (append one line per milestone)
- (start) 2026-06-13 night — handoff written; 304 tests green on `development`.
- 2026-06-14 — §9a **Mail model + DispatchMailMessage builder DONE**. Decoded the full mail wire grammar from the
  get-mail capture (629 B base / 754 B w/ attachment); added `Mail`+`MailAttachment` tables (migration `AddMail`),
  `MailMessageCodec` (Build/Parse, byte-exact), 4 golden tests in `MailMessageCodecTests`. 308 tests green.
- 2026-06-14 — §9a **AH listings importer DONE**. `CaptureListingImporter` harvests the captured 0x2005 search-result
  rows into `TradepostListing` (242 listings / 11 item ids seeded into faolan.db) so AH search returns real data;
  round-trip test `CaptureListingImporterTests`; importer `--listings-only` flag re-seeds just these (kept the db
  diff clean — a full re-import would churn Items/NpcSpawns from newer captures). 311 tests green.
- 2026-06-14 — §4b/§7(c) **method-name logging DONE**. `GameServerMethodNames.Resolve(receiverClass, opcode)` names
  the multiplexed GameServer RPC by receiver-class catalog; the GameServer `default` branch now logs the method name
  instead of bare hex. Test `GameServerMethodNamesTests`. 318 tests green.
- 2026-06-14 — §7 **dead `AdjustSnapshotPrefixRootLength` removed**. Migrated the end-to-end reassembly test into
  `SnapshotBlobLengthTests` (BE32 model), dropped the two redundant varint-invariant tests + the dead method +
  `SnapshotPrefixLengthTests.cs`. 316 tests green.
- 2026-06-14 — §7 **rename `BigboyTailsFile` → `SnapshotTails`** (file + class, 5 files; no behavior change). 316 green.
- 2026-06-14 — §7 **fold `Packets.cs` sender duplication**: only `ExistingCharacterWorldEntryPromptSender` was
  byte-identical to `WorldFraming.Sender` (now aliases it); the other senders differ (class/sub-id) and stay. 316 green.
