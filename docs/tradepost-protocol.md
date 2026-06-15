# Tradepost (auction house) + mail protocol

> **STATUS (2026-06-12): ⛔ PINNED — awaiting an in-client render check.** The protocol below is decoded
> byte-exact and the open + search replies are wired + golden-tested (`Faolan/GameServer/Economy/Tradepost/`,
> `TradepostResponseTests`). What's unverified is whether the client actually renders the AH window/listings —
> test with the `.ah` GM command (sends `DispatchOpenTradepost` at the player's position). Once it renders,
> proceed to Step 3 (DB-backed listings + the `Mail`/`MailAttachment` model + buy→mail). See `TODO.md` §9a.

Decoded 2026-06-12 from two captures via `Captures/decode_tradepost.py`:
- `account-faolan-character-necrol80-...-sell_items_to_trader-intract-with-auctionhouse` (browse/search)
- `account-faolan-character-assassinl23-...-buy_from_autionhouse_get_mail-...` (buy + mail delivery)

The auction house is AoC's **Tradepost**, carried on the **GameServer (port 7030)** RPC layer — *not* a separate
connection. Mail rides the **same** agent, and a bought item is delivered to the buyer **via mail**.

## RPC framing (generic — applies to all GameServer RPC, reusable)

```
[len:u32 BE][callId:u32 BE][innerLen:u32 BE][ RpcHeader protobuf ][ method:u16 BE (0x20xx) ][ body ]
```
- `innerLen` covers the RpcHeader **+ the 2-byte method only**; the `body` follows *after* it (`raw[12+innerLen:]`).
- **RpcHeader** = protobuf `{ field1 (0x0a) = Receiver, field2 (0x12) = Sender }`, each a `ProtoEndpoint`
  sub-message `{ 0x0d fixed32 classId (LITTLE-endian on wire), 0x10 varint instance }`
  (matches memory `protocol_receiver_varint`).
- Tradepost class ids: **TradepostAgent `0x3D744981`** (client→server), **TradepostInterface `0xF2C2D5B5`**
  (server→client). On S→C the receiver endpoint is the player's session class `0xA9ECB95D` (GameCharInterface inst).
- The `method` is a **per-class selector** in the `0x20xx` range (NOT a name-hash, NOT a GameServer transport opcode).

## Open trigger (important)

`DispatchOpenTradepost` is **not** a reply to `TradepostOpen` — the capture shows the opposite order: the player
**interacts with the auctioneer NPC** (`Interact 0x642CD3D6`) → the server sends `DispatchOpenTradepost` (+ a
`BankIIR 0x0BA1C1CB`) → *then* the client opens the window and sends `TradepostOpen`, then searches. So the open is
triggered by the NPC interaction (PoC: the `.ah` GM command sends it at the player's position); replying to
`TradepostOpen` would be circular and never bootstraps the window. The access-point `maxRadius` (15.0) is a
client-side proximity check, so the access position must be within 15 units of the player.

## Method map

| dir | method | name | body |
|-----|--------|------|------|
| C→S | `0x2011` | TradepostOpen | empty |
| C→S | `0x200D` | **TradepostItemSearch** | the query (below) |
| C→S | `0x200E` | **TradepostBuyItem** | `[saleId:u64][searchResultId:u64]` |
| C→S | `0x2010` | TradepostClose | empty |
| C→S | `0x2001/2007/2006/2003/2000` | mail ops (GetMail / GetMailItems / GetMailItem / Delete-or-Mark / poll) | `[mailId:u64]`, or `[mailId][itemId]` for 0x2006 |
| S→C | `0x2000` | **DispatchOpenTradepost** | 4 floats `[posX][posY][posZ][maxRadius=15.0]` (big-endian raw) |
| S→C | `0x2005` | **DispatchTradepostSearchResults** | listing rows (below) |
| S→C | `0x2002` | **DispatchMailMessage** | the mail (below) |
| S→C | `0x2003` | DispatchSendMailResult | `[bSuccess][cError]` |
| S→C | `0x2004` | DispatchRetrieveMailResult (was nicknamed "ack") | `01` |
| S→C | `0x2001` | DispatchClose | empty (no-arg; the catalog-missed insertion) |
| S→C | `0x2006/0x2007` | DispatchDeleteCanceledMail / DispatchDeleteCanceledMailItem | — |

(Full TradepostInterface table — 8 methods, recovered from the dispatcher switch — in `docs/aoc-rpc-method-opcodes.md`.)

## TradepostItemSearch query (C→S `0x200D`, byte-exact)

```
[strlen:u16][lang ascii e.g. "en"]
[cat:u32 = 2018 (0x7E2)]      # category root, constant in these captures
[page:u32]                    # result page (1,2,3…)
[cat2:u32 = 2018]
[itemId:u32]                  # the item template being searched (0x7530, 0x754D, …)
[filter: 20 bytes of 0xFF]    # wildcard = no filter
[priceRange: 16 bytes = 0]    # min/max, 0 = any
[filter2: 8 bytes 0xFF][0x00…]
```

## DispatchTradepostSearchResults rows (S→C `0x2005`) — VALIDATED byte-exact (full grammar 2026-06-13)

```
[head:u32]                    # the query's category root (0x7E2 in the captures); echoed, NOT a quantity
[ row ]*                      # rows concatenated, sorted by price ASCENDING
```
Per row (all integers big-endian):
```
[saleId:u64]                  # unique listing id; the buy targets this
[handle:u32]
[nameLen:u16][seller ascii]
[price:u64]
[qty:u32]
[itemId:u32]                  # the item template the listing is filed under (= the query's item)
[instanceField:u64]           # 0 in the capture (buyout/reserved — undecoded, preserved verbatim)
[itemHandle:u32]              # an item-handle-like u32 (undecoded, preserved verbatim)
[entryLen:u32][ProtoInventoryGameEntry]   # the listed item, length-prefixed (NOT a bare/self-delimited proto)
```
The trailing entry is the **same `ProtoInventoryGameEntry`** the FullChar snapshot / shop window / live add-item
use — so one codec serves them all (`TradepostListingCodec`, byte-exact golden test `TradepostListingCodecTests`):
- `item_id` = the saleId
- `stat_list` = `{0: flags (0x421), 26: qty, 258: price, 265: timestamp/expiry}` (the `0`/`265` semantics are still
  loose, kept verbatim per listing like `ShopItem.Stat356`)
- `item_data` = the listed **instance's** `ProtoAcgItem` (its own Data1..4 quad + Template1/2 + Level + Hash). The
  instance's `Template` (0x3E0A97 in the capture) differs from the row's `itemId` (0x755D), so the listed identity
  lives on the listing row (`TradepostListing` table), not via the `Items` template table.

Validated byte-exact on pkt#1028 (seller "Jocko", item 0x755D, price 1,000,000, qty 1). The earlier "bare
self-delimited ProtoAcgItem / split on the `20 d8 2b` saleId prefix" note was the Python dissector's display
heuristic; the real entry boundary is the explicit `entryLen` u32, so C# reads it without prefix-sniffing.

**Status (2026-06-13):** DB-backed search is WIRED — `TradepostItemSearch` queries `TradepostListing` rows by the
searched `itemId` (cheapest first) and builds the grid byte-exact via `TradepostListingCodec.Build`; the captured
single-row replay is the fallback when no listing matches (so the grid still renders pre-import). Still to build:
a listings importer/seed (harvest the captured search rows into `TradepostListing`), SELL into the AH (create a
listing from a bank item), and BUY → mail delivery.

## Buy → mail delivery (confirmed)

`TradepostBuyItem(0x200E)` with the chosen `saleId` → the server pushes **`DispatchMailMessage`(0x2002)** to the
buyer. The bought item is the mail's attachment; the client then runs the mail ops
(`GetMailItems`/`GetMailItem`/Delete) to retrieve it.

```
DispatchMailMessage body:
[mailId:u64]
[recipient endpoint: class:u32 (0x08006F31) ][nameLen:u16][recipient ascii]   # = the buyer
[sender    endpoint: class:u32 (0x08006F31) ][nameLen:u16][sender ascii]      # EMPTY for an AH delivery (system mail)
[remoteformat XML body]
```
**The recipient is the buyer (e.g. "AssassinL23"); the sender is empty** — an auction-house delivery is a *system*
mail, not "from self". The body is a remoteformat string:
`<remoteformat token="TradePostGUI_MailItemBought" category="629" key="…"><parameter value="7;AssassinL23"/>
<parameter value="1;<itemInstanceId>"/><parameter value="7;&lt;remoteformat id=&quot;<itemId>&quot;…"/>` — i.e.
the buyer name, the bought item's instance id, and a nested remoteformat describing the item.

### Mail attachment (the bought item)

The base `DispatchMailMessage` has no item; the client fetches it with `GetMailItem(0x2006)` and the server
replies with a `DispatchMailMessage` that has the attachment **appended** (verified: 629 B without → 754 B with,
only the tail differs):
```
…[base mail body]…
[0x01]                        # has-attachment flag
[itemInstanceId:u64]          # = the bought saleId (20d82b62d02b198b)
[protoLen:u32]
[item: ProtoAcgItem protobuf] # same item proto as the search rows
[footer]
```
So both the listing rows and the mail attachment carry the **same `ProtoInventoryGameEntry`** (item_id =
itemInstanceId, stat_list = `{26: qty}`, item_data = the item's `ProtoAcgItem`) — one reusable builder serves
search results, the AH window, and the mail item.

**Status (2026-06-13):** the `Mail` + `MailAttachment` DB model and the **`DispatchMailMessage` builder are DONE +
byte-exact** (`MailMessageCodec.Build`, `Faolan/Migrations/*AddMail`). Full wire grammar decoded from the get-mail
capture (the 629 B base #1604/#1611 and the 754 B attachment #1607):
```
[mailId:u64]
[recipientClass:u32][nameLen:u16][recipient ascii]        # recipientClass = 0x08006F31 (const)
[senderClass:u32][nameLen:u16][sender ascii]              # sender NAME empty = system mail
[xmlLen:u16][remoteformat XML]
[footerWord:u32 = 0x0B][money:u64][ts1:u32][ts2:u32]
[attachCount:u32]
attachCount x { [itemInstanceId:u64][entryLen:u32][ProtoInventoryGameEntry][trailer: u32 0x72, u32 0x08006F31, u32 0] }
```
Golden tests `MailMessageCodecTests` (Build == captured 629 B and 754 B; `includeAttachments=false` reproduces the
base from the attachment mail).

**Status (2026-06-14): BUY → mail delivery + the mailbox ops are WIRED** (`Faolan/GameServer/Economy/Tradepost/TradepostService.cs`,
`TradepostResponses.SendMailMessage/SendMailResult/SendRetrieveMailResult`). `TradepostBuyItem(0x200E)` debits the
buyer (`DispatchUpdateCash`), creates a **system mail** (recipient = buyer, sender empty) with the listed item
escrowed as a `MailAttachment`, deletes the listing, and pushes the base `DispatchMailMessage`. The mailbox ops then
work off the DB-backed mail: `GetAllMails` (poll → each mail base + `DispatchRetrieveMailResult`), `GetMailByID`
(base), `GetItemData 0x2007` (preview = mail WITH attachment), `GetMailItem 0x2006` (TAKE = grant the item to the
bag via the proven `GrantItemToBackpack`/`DispatchAddItem 0x2020` render path, then re-send the now-itemless mail),
`MarkMailRead` (footer 0x0b→0x09), `DeleteMail`. Repo methods + `TradepostMailBody` (the `TradePostGUI_MailItemBought`
remoteformat XML, **byte-exact for the captured inputs** — `TradepostMailBodyTests`); the mail-flow ordering matches
the capture (BuyItem F502 → mail poll/preview/take F509–F557). IN-CLIENT GATE: buy a listing → coins deducted, a
mail arrives, opening it shows the item, "take" puts it in the bag. KNOWN INTERIMS: the mail-body **item-name**
fragment is left empty (per-item RDB name key unsynthesizable — same client-RDB-by-id limit as conversation text);
the granted item uses the **Items-template** identity, not the listed instance's exact Data quad (like vendor BUY);
the seller isn't credited (importer-seeded sellers aren't live characters).

## SELL into the AH + player SendMail (decoded 2026-06-14 from `account-faolan-character-necrol80-sell_item_on_ah-send_mail_with_attachment`)

**`TradepostSellItem` (C→S `0x200A`) — byte-exact (capture F375):**
```
[nBankItemSlot:u32][nPrice:u64]
```
F375 = slot 6 @ 1,000,000 tin. Lists one of the player's BANK items (container 105) for sale: the server creates a
`TradepostListing` from the bank item's identity (Data quad + template + level + archetype, seller = char name, the
given price/qty + the origin **bank slot**). The listed item **STAYS in its bank slot** (the client just draws a
"listed" overlay - it is NOT escrowed out); it only leaves the seller's bank when someone BUYS it (the buy removes
the seller's bank cell by `(SellerName, BankSlot)`). There is NO direct tradepost reply (the client re-polls).

The seller addresses their own listing by the **origin bank slot** (NOT the saleId - that's for buying), so the
listing carries a `BankSlot` (migration `AddTradepostListingBankSlot`).

| dir | method | name | body | behaviour |
|-----|--------|------|------|-----------|
| C→S | `0x200A` | TradepostSellItem | `[nBankItemSlot:u32][nPrice:u64]` | list the bank item; it stays in the slot |
| C→S | `0x200B` | TradepostChangeItemPrice | `[nBankItemSlot:u32][nNewPrice:u64]` | reprice the seller's listing (F363: slot 6 → 5,000,000) |
| C→S | `0x200C` | TradepostCancelSellItem | `[nBankItemSlot:u32]` | delete the listing; item stays in the bank (F397: slot 6) |
| C→S | `0x2009` | CancelMail | `[mailId:u64]` | recall/delete a mail (F403) |

WIRED (`GameServerListener.HandleTradepostSell`/`HandleTradepostChangePrice`/`HandleTradepostCancelSell`/
`HandleTradepostCancelMail`, repo `CreateTradepostListing`/`GetTradepostListingBySellerSlot`/`SetTradepostListingPrice`,
golden tests `TradepostSellBodyTests`). INTERIM: the listing's search `ItemId` is filed under the item's TEMPLATE (we
lack the retail item-type search id), so a normal item-type search won't necessarily surface a player listing yet.

**`SendMail` (C→S `0x2002`) — player→player mail with a bank-item attachment (capture F499), WIRED
(`HandleTradepostSendMail`): creates a Mail to the recipient; the attached item STAYS in the sender's bank with a
**mail-pending overlay** (`0x201c` stat 0 = flags **`0x8001`**, capture F500), it only leaves the sender's bank when
the recipient TAKES it (and the overlay clears if the sender CancelMails it - F404 flag → `1`). Replies
`DispatchSendMailResult`; an unknown recipient is rejected (success=0). The mail remembers the sender's origin bank
slot (`Mail.SenderBankSlot`, migration `AddMailSenderBankSlot`). Grammar:**
```
[u16+recipientName ascii][u16+bodyText ascii][money:u64][0x000007E2:u32][bankItemSlot:u32]
```
F499 = to "DemoL80", body "Hello this is a test", money 0, attach bank slot 5 (the `0x7E2` word is the same category
constant the AH search carries; preserved). Reply `DispatchSendMailResult` (S→C `0x2003`) = `[u8 success][u16+remoteformat XML]`
(F500: success=1 + a result-message remoteformat). This is the player-composed mail path (distinct from the
AH buy→system-mail already wired); to implement, create a `Mail` to the recipient with the bank item escrowed.

## Implementation notes (for TODO §9a)

- No new connection/server: handle these on the existing GameServer dispatcher (mirror the IIR sub-op handlers).
- A `Mail` + `MailAttachment` DB model (sender, recipient, body token + args, attached money/items, read flag);
  buy = create a mail to the buyer with the item escrowed → grant on `GetMailItem`.
- A `TradepostListing` table (saleId, sellerName, itemId, price, qty, expiry) drives search; `DispatchOpenTradepost`
  needs the access NPC's position + a 15.0 radius.
- Capture-replay PoC first (replay a captured `0x2005` so the AH window renders), then go DB-backed.
