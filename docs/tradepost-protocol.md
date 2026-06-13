# Tradepost (auction house) + mail protocol

> **STATUS (2026-06-12): ⛔ PINNED — awaiting an in-client render check.** The protocol below is decoded
> byte-exact and the open + search replies are wired + golden-tested (`Faolan/GameServer/Tradepost/`,
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
base from the attachment mail). Still to wire: SELL into the AH (create a listing + escrow) and BUY → mail
delivery (debit buyer, create a system mail with the item escrowed, grant on retrieve).

## Implementation notes (for TODO §9a)

- No new connection/server: handle these on the existing GameServer dispatcher (mirror the IIR sub-op handlers).
- A `Mail` + `MailAttachment` DB model (sender, recipient, body token + args, attached money/items, read flag);
  buy = create a mail to the buyer with the item escrowed → grant on `GetMailItem`.
- A `TradepostListing` table (saleId, sellerName, itemId, price, qty, expiry) drives search; `DispatchOpenTradepost`
  needs the access NPC's position + a 15.0 radius.
- Capture-replay PoC first (replay a captured `0x2005` so the AH window renders), then go DB-backed.
