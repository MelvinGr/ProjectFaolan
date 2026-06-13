# What the RPC-opcode recovery unblocked — handoff (2026-06-13)

The 16-bit RPC method tables are now fully recovered in **both directions** (`docs/aoc-rpc-method-opcodes.md`;
scripts/outputs in `Captures/ghidra/`). `wire = 0x2000 + registration index`, base `0x2000` for every interface
except PlayerAgent/PlayerInterface. Crucially, for every **named** s2c method we also already have the **wire
field order** (param names) from `docs/aoc-client-rpc-methods.json` — so most of these are *buildable*, not just
identified. This handoff lists what that opens up, with the concrete opcode + body for each.

**The standing rule still applies:** name + field order is necessary, not sufficient — each new builder must be
proven **byte-exact against a capture** (golden test in `Faolan.Tests`) before it ships. Field *types* (the
`i_c`/`i_n`/`i_v` Hungarian legend → ID32 / int / vector) tell you the shape; the capture nails the encoding.

---

## STATUS UPDATE (2026-06-13) — what's been done since this handoff

- **§1 DispatchAddItem (0x2020) — DONE + IN-CLIENT VERIFIED.** Decoded, byte-exact golden test
  (`DispatchAddItemTests`), wired into a shared `GrantItemToBackpack` (persist + live add) reused by `.additem`,
  loot, and `.spawnbag`. A connected player received granted gear live with no relog. Memory
  `[[protocol_containeradditem]]` updated. Body confirmed: `[ID32 invType,charId][u32 pos][u32-len entry][u32
  feedback]`, entry = the bare `ProtoInventoryGameEntry` (the snapshot's `.Item`, no Pos wrapper).
- **§3 Loot item GRANT — DONE.** `TryLootAll (0x2043)` → `GrantItemToBackpack` (live DispatchAddItem) for each bag
  item; `TryLootCash (0x2045)` → wallet credit. Loot bags carry random items. ⚠️ Caveat: the loot **bag/corpse
  visual doesn't render** — that's the separate mid-session-spawn blocker (`[[protocol_midsession_spawn_blocked]]`
  / `docs/mid-session-spawn-blocker.md`), NOT a loot-protocol problem; the grant path works via loot-all.
- **Suggested order steps 1 & 2 — DONE** (DispatchAddItem builder + loot grant). Steps 3 (vendor BUY) and 4
  (mail/AH) are still open.
- **The full s2c Dispatch tables were added to `Opcodes.cs`** as enums (`GameCharInterfaceDispatchOpcodes` etc.,
  generated from `Captures/ghidra/s2c_tables.json`) — relevant to §7 (named routing).
- **Bonus, unrelated to the table:** the personal BANK storage was decoded + delivered (BankIIR 0x0BA1C1CB,
  container kind 105, in-client verified) — `[[protocol_bank_storage]]`.

Still open from this handoff: §2 vendor BUY (gate gone, not built), §4 spell/feat grants, §5 trade/quest-share,
§6 mail, §7 unknown-(class,opcode) name logging.

---

## 1. Incremental inventory delivery — the big one (corrects a prior dead end)  ✅ DONE (see status update above)

`[[protocol_containeradditem]]` recorded "no server add-item packet; the world-entry snapshot is the only
delivery." **That is now disproven.** GameCharInterface has a full incremental inventory s2c family:

| opcode | method | body (wire field order) |
|--------|--------|--------------------------|
| `0x2020` | **DispatchAddItem** | `i_cInventoryID`, `i_nItemPos`, `i_cEntry`, `i_nFeedback` |
| `0x201F` | DispatchRemoveItem | `i_cInventoryID`, `i_nItemPos`, `i_nAmountToRemove` |
| `0x201D` | DispatchAddToItemSlot | (slot add) |
| `0x201E` | DispatchRemoveFromItemSlot | (slot remove) |
| `0x2009` | DispatchChangeItemStackSize | (stack count update) |
| `0x201C` | DispatchSetItemStat | (per-item stat update) |

`i_cEntry` is the same item-entry blob `SnapshotItemBuilder` already produces for the world-entry snapshot, so an
add-item builder can reuse that codec. This is the missing piece for making **loot, buy, quest-reward, and mail**
actually grant an item *live* (mid-session) instead of only appearing after a relog/snapshot rebuild. Next step:
find a capture that contains a `0x2020` on GameCharInterface (loot or vendor-buy), confirm the `i_cEntry` layout
matches the snapshot entry, build `DispatchAddItem`, golden-test it, then wire it into the loot/buy paths.

---

## 2. NPC vendor BUY side — §9 economy (was "item-grant-gated")

The economy queue (`[[project_economy_queue]]`) had SELL + money done and **BUY blocked on item-grant**. The
whole shop loop is now named s2c on GameCharInterface:

| opcode | method |
|--------|--------|
| `0x2033` | DispatchShopData (already used) |
| `0x2034` | DispatchCloseShop |
| `0x2035` | DispatchItemSoldToShop (already used) |
| `0x2036` | **DispatchItemBoughtBack** — `inventoryID`, `inventoryPos`, `itemEntry` |
| `0x2005` | DispatchUpdateCash — `i_nCash`, `i_nFeedback` (the wallet, already wired) |

So **BuyItem (c2s `0x2033`) → DispatchAddItem (`0x2020`) + DispatchUpdateCash (`0x2005`)** is now a fully-named
round-trip. The item-grant that gated BUY is exactly §1. Implement BUY after the add-item builder lands.

---

## 3. Loot item + cash grant — completes `[[protocol_loot_interaction]]`  ✅ GRANT DONE (bag VISUAL blocked, see status update)

That memory left "item-grant still capture/RE-gated" and "`0x2045` TryLootCash UNHANDLED = coins never granted."
The loot s2c reply family is now named: `0x2037` DispatchOpenLootBag (`i_cLootBagID`), `0x2038` DispatchCloseLootBag,
`0x2027`–`0x202C` DispatchReplyLootForCharacter\* (template/hash/cluster replies), `0x2039`–`0x203F` need/greed +
master-looter dialogs. Combined with DispatchAddItem (`0x2020`) for the item and DispatchUpdateCash (`0x2005`) for
coins, **TryLootAll / TryLootCash can finally grant**. Build order: DispatchAddItem first (§1), then answer
TryLootAll with add-item(s), TryLootCash with a cash update.

---

## 4. Spell / ability / feat grants — proper, not snapshot-only

- `0x200A` **DispatchLearnSpell** — `i_nSpellID` (the real spell-grant the RE flagged long ago; now has its opcode)
- `0x200B` DispatchForgetSpell
- `0x2052` DispatchAddSpecialAbility (already used by `GrantStartingAbilities` — confirmed correct)
- `0x2053` DispatchRemoveSpecialAbility · `0x2011` DispatchUntrainSingleFeat
- `0x209A/0x209B` DispatchPassiveAbilityEquip / Unequip

Unblocks granting spells/feats on level-up or quest-reward without rebuilding the whole snapshot.

---

## 5. Player↔player trade, quest sharing, team quests

Full named families now exist: trade `0x2068`–`0x2074` (RequestTrade / StartTrade / cash / add/remove item /
accept), quest-share `0x2047`–`0x204C` (NextQuestAvailable / QuestShare\* / UpdateTeamQuestList). These were never
implemented; they're now fully specified for whenever grouping/trading is picked up.

---

## 6. Mail (TradepostInterface) — both directions named

`docs/tradepost-protocol.md` + `TradepostProtocol.InterfaceMethod` now carry the true 8-method s2c table
(DispatchMailMessage `0x2002`, DispatchSendMailResult `0x2003`, DispatchRetrieveMailResult `0x2004`,
DispatchDeleteCanceledMail/Item `0x2006/0x2007`). With the c2s mail ops already named, the mail subsystem of §9
(GetAllMails / SendMail / GetMailItems → DispatchMailMessage, attachment via DispatchAddItem) is fully specified.

---

## 7. The `default`-branch "unknown (class, opcode)" log can now resolve names

Every inbound `(receiver class, opcode)` and every outbound s2c opcode maps to a real method name in both
directions (`docs/aoc-rpc-method-opcodes.md`). The TODO §4b "log unknown (class, method) combos" idea can print
the actual method name instead of a bare hex value — useful for triaging the named-but-unhandled client packets.

---

## Still gated / caveats (read before relying on a table entry)

- **Body layout still needs a capture.** The tables give name + field *order*; the byte encoding (varint vs
  fixed, endianness, optional fields) must be confirmed against a capture per the construct-don't-replay loop.
  Methods that are NOT in `aoc-client-rpc-methods.json` s2c (the no-arg "insertion" slots — 24 in
  GameCharInterface, 33 in CSPlayerInterface) have a known opcode but no fields and no string name; don't guess.
- **Receiver class matters.** The GameServer connection multiplexes GameAgent/GameCharAgent/GameCharAgentProject/
  Tradepost; the same opcode is a different method per class. Build/route by `(class id, opcode)`. Most world
  downloads are GameCharInterface (`0x8B87F791`).
- **A few old server names were content-guesses and are wrong** (annotated in `Opcodes.cs`
  `GameServerRespondOpcodes`): the values are capture-replayed so they still *work*, but e.g. `0x2009` is
  DispatchChangeItemStackSize (not "Pong"; real per-char pong = `0x2082`), `0x2008` = DispatchSetQuestMarker,
  `0x2024` = DispatchValidResurrectionSpots. Don't rename the working sends, but don't trust the nickname either —
  if you touch those code paths, verify against the recovered table + a capture.
- **PlayerInterface / PlayerAgent (login) stays behavior-derived.** Its dispatcher uses a 32-bit *hash* method id
  (binary-search tree, e.g. `0x5dc18991` = DispatchAuthenticateAck), not `0x2000 + index`, and the hash fn isn't
  plain BKDR-31/CRC32. Login already works, so this is low priority; the handler→name pairs in
  `docs/aoc-rpc-method-opcodes.md` are known plaintext↔hash anchors if someone wants to crack it.

## Suggested order of attack
1. ✅ **DONE — DispatchAddItem (`0x2020`) builder + golden test** — the keystone for §1/§2/§3. Built, golden-tested
   (`DispatchAddItemTests`), in-client verified, wired into `GrantItemToBackpack`.
2. ✅ **DONE — loot grant** (TryLootAll → add-item, TryLootCash → wallet credit). NB the loot bag VISUAL is blocked
   by the mid-session-spawn issue, but the grant works.
3. ⬜ Vendor BUY (BuyItem → DispatchItemBoughtBack/AddItem + cash) — gate is gone (DispatchAddItem); not built.
4. ⬜ Mail/AH delivery (§9) reuses the same add-item path for attachments — AH window + bank now open in-client;
   AH search/sell + mail still to build.
