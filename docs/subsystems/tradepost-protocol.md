# Tradepost (auction house) + mail protocol

The protocol below is decoded byte-exact, and the tradepost and mailbox are served from the database
(`Faolan/GameServer/Economy/TradepostService.cs` + `Faolan.Core.Network/Codecs/Economy/`, `TradepostResponseTests`).
The `.ah` GM command sends `DispatchOpenTradepost` at the player's position.

Decoded from two captures via `TradepostListingCodecTests`:
- `account-faolan-character-necrol80-...-sell_items_to_trader-intract-with-auctionhouse` (browse/search)
- `account-faolan-character-assassinl23-...-buy_from_autionhouse_get_mail-...` (buy + mail delivery)

The auction house is AoC's **Tradepost**, carried on the GameServer (port 7030) RPC layer — not a separate
connection. Mail rides the same agent, and a bought item is delivered to the buyer via mail.

## RPC addressing

The ordinary RpcHeader ([`../protocol/02-rpc.md`](../protocol/02-rpc.md)); the method is the RpcHeader's
`method_instance` varint, rendered `0x20NN` in a dump. Tradepost class ids: **TradepostAgent `0x3D744981`**
(client→server), **TradepostInterface `0xF2C2D5B5`** (server→client). On S→C the receiver endpoint is the player's
session class `0xA9ECB95D` (GameCharInterface inst). The method ids collide with ordinary GameServer methods, so
tradepost packets route by RPC class id (`TradepostAgentType`), never by method alone.

## Open trigger

`DispatchOpenTradepost` is not a reply to `TradepostOpen` — the capture shows the opposite order: the player
interacts with the auctioneer NPC (`Interact 0x642CD3D6`) → the server sends `DispatchOpenTradepost` (+ a
`BankIIR 0x0BA1C1CB`) → then the client opens the window and sends `UpdateItemTimeouts` (`0x2011`, empty body — the
AH-window-open op; the nominal `TradepostOpen 0x200F` is not observed in the captures), then searches. So the open is
triggered by the NPC interaction; replying to the client's window-open op would be circular and never bootstraps the
window. The access-point `maxRadius` (15.0) is a client-side proximity check, so the access position must be within
15 units of the player.

**Which NPCs are auctioneers** comes from the client's own role tag, not from a display name:
`ProtoCoreCharacterIIR.NpcStats.profession_type` (stored as `NpcStatCore.ProfessionType`, `NpcProfession.Auctioneer`
= 1), checked by `TradepostService.IsAuctioneerTemplate`. It is exclusive - the four templates carrying it (8554,
8675, 11768, 14712) are exactly the four named "Trader" in the world, and nothing else carries it. The name is the
wrong key: "Trader" is a role that four distinct templates play, so it pins none of them, and any other NPC that
happened to carry the string would open the auction house.
[V3: NpcProfessionTests.Auctioneer_IsExactlyTheTradersAndNothingElse]

## Method map

| dir | method | name | body |
|-----|--------|------|------|
| C→S | `0x2011` | **UpdateItemTimeouts** (the AH-window-open op; `TradepostOpen 0x200F` is not observed) | empty |
| C→S | `0x200D` | **TradepostItemSearch** | the query (below) |
| C→S | `0x200E` | **TradepostBuyItem** | `[saleId:u64][searchResultId:u64]` |
| C→S | `0x2010` | TradepostClose | empty |
| C→S | `0x2000` / `0x2001` / `0x2003` / `0x2006` / `0x2007` / `0x2008` | mail ops (GetAllMails poll / GetMailByID / MarkMailRead / GetMailItem / GetItemData / DeleteMail) | `[mailId:u64]`, or `[mailId][itemId]` for 0x2006 |
| S→C | `0x2000` | **DispatchOpenTradepost** | 4 floats `[posX][posY][posZ][maxRadius=15.0]` (big-endian raw) |
| S→C | `0x2005` | **DispatchTradepostSearchResults** | listing rows (below) |
| S→C | `0x2002` | **DispatchMailMessage** | the mail (below) |
| S→C | `0x2003` | DispatchSendMailResult | `[bSuccess][cError]` |
| S→C | `0x2004` | DispatchRetrieveMailResult | `01` |
| S→C | `0x2001` | DispatchClose | empty (no-arg; the catalog-missed insertion) |
| S→C | `0x2006/0x2007` | DispatchDeleteCanceledMail / DispatchDeleteCanceledMailItem | — |

(The full tables are `TradepostAgentMethods` / `TradepostInterfaceMethods` in `Faolan.Core/Enums/Opcodes.cs`; the
dispatcher-switch recovery is in `docs/re/aoc-rpc-method-opcodes.md`.)

## TradepostItemSearch query (C→S `0x200D`, byte-exact)

```
[strlen:u16][lang ascii e.g. "en"]
[cat:u32 = 2018 (0x7E2)]      # in the REQUEST its meaning is untested (a category root, or the same 0x3F1
                              #   list framing with one element). The RESPONSE head below is a count.
[page:u32]                    # result page (1,2,3…)
[cat2:u32 = 2018]
[itemId:u32]                  # the item template being searched (0x7530, 0x754D, …)
[filter: 20 bytes of 0xFF]    # wildcard = no filter
[priceRange: 16 bytes = 0]    # min/max, 0 = any
[filter2: 8 bytes 0xFF][0x00…]
```

## DispatchTradepostSearchResults rows (S→C `0x2005`) — byte-exact

```
[head:u32]                    # the 0x3F1 LIST COUNT: (rowCount+1) * 0x3F1
[ row ]*                      # rows concatenated, sorted by price ascending
```

`head` is the protocol's shared `0x3F1` list framing — `(rowCount+1) * 0x3F1` — the same word
`GuildSearchResultCodec`, `TradeskillReplyCodec`, the quest progress-sublist (`docs/re/questc-region-decode.md`) and
`ValidResurrectionSpotsCodec` carry. It is not a category echo, although two coincidences make it look like one: the
search request also carries `0x7E2`, and a 1-row result is `2 * 0x3F1 = 0x7E2` exactly. Across the captures' 15
search results an empty grid is a 4-byte body whose head is `0x3F1` (= 0 rows) with no rows at all, and head tracks
the count all the way up (`0x17A6` = 6× → 5 rows, `0x1375F` = 79× → 78 rows).
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
- `stat_list` = `{0: flags (0x421), 26: qty, 258: price, 265: timestamp/expiry}` (the `0`/`265` semantics are
  loose, kept verbatim per listing like `ShopItem.Stat356`)
- `item_data` = the listed **instance's** `ProtoAcgItem` (its own Data1..4 quad + Template1/2 + Level + Hash). The
  instance's `Template` (0x3E0A97 in the capture) differs from the row's `itemId` (0x755D), so the listed identity
  lives on the listing row (`TradepostListing` table), not via the `Items` template table.

Validated byte-exact on pkt#1028 (seller "Jocko", item 0x755D, price 1,000,000, qty 1). The entry boundary is the
explicit `entryLen` u32, not a `20 d8 2b` saleId prefix, so the entry is read without prefix-sniffing.

`TradepostItemSearch` queries the realm's `TradepostListing` rows by the searched `itemId` (cheapest first) and
builds the grid via `TradepostListingCodec.Build`; no match yields an empty grid.

## Buy → mail delivery

`TradepostBuyItem(0x200E)` with the chosen `saleId` → the server pushes **`DispatchMailMessage`(0x2002)** to the
buyer. The bought item is the mail's attachment; the client then runs the mail ops to retrieve it.

The recipient is the buyer (e.g. "AssassinL23"); **the sender is empty** — an auction-house delivery is a system
mail, not "from self". The body is a remoteformat string:
`<remoteformat token="TradePostGUI_MailItemBought" category="629" key="…"><parameter value="7;AssassinL23"/>
<parameter value="1;<itemInstanceId>"/><parameter value="7;&lt;remoteformat id=&quot;<itemId>&quot;…"/>` — i.e.
the buyer name, the bought item's instance id, and a nested remoteformat describing the item.

The base `DispatchMailMessage` has no item; the client fetches it with `GetMailItem(0x2006)` and the server replies
with a `DispatchMailMessage` that has the attachment appended (629 B without → 754 B with, only the tail differs).
Both the listing rows and the mail attachment carry the same `ProtoInventoryGameEntry` (item_id = itemInstanceId,
stat_list = `{26: qty}`, item_data = the item's `ProtoAcgItem`).

### `DispatchMailMessage` grammar

Decoded from the get-mail capture (the 629 B base, pkts 1604/1611, and the 754 B attachment, pkt 1607):
```
[mailId:u64]
[recipientClass:u32][nameLen:u16][recipient ascii]        # recipientClass = 0x08006F31 (const)
[senderClass:u32][nameLen:u16][sender ascii]              # sender NAME empty = system mail
[xmlLen:u16][remoteformat XML]
[footerWord:u32 = 0x0B][money:u64][ts1:u32][ts2:u32]
[attachCount:u32]
attachCount x { [itemInstanceId:u64][entryLen:u32][ProtoInventoryGameEntry][trailer: u32 0x72, u32 0x08006F31, u32 0] }
```
`MailMessageCodec.Build`; `Mail` / `MailAttachment` tables in `Faolan.Core.Database/Tables/`. Golden tests
`MailMessageCodecTests` (Build == captured 629 B and 754 B; `includeAttachments=false` reproduces the base from the
attachment mail).

### What the server does

`TradepostBuyItem(0x200E)` debits the buyer (`DispatchUpdateCash`), creates a system mail (recipient = buyer, sender
empty), deletes the listing, and pushes the base `DispatchMailMessage`. The listed item moves out of the seller's bank
into the buyer's Mail container (114) as the escrowed attachment — the row is handed over, so the crafter and every
socketed gem travel with it. An importer-seeded listing has no seller character and no backing row; its mail keeps a
null escrow and the take mints from the template.

The mailbox ops work off the DB-backed mail: `GetAllMails` (poll → each mail base + `DispatchRetrieveMailResult`),
`GetMailByID` (base), `GetItemData 0x2007` (preview = mail with attachment), `GetMailItem 0x2006` (take = grant the
item to the bag via `GrantItemToBackpack` / `DispatchAddItem 0x2020`, then re-send the now-itemless mail),
`MarkMailRead` (footer 0x0b→0x09), `DeleteMail`. `TradepostMailBody` builds the `TradePostGUI_MailItemBought`
remoteformat, byte-exact for the captured inputs (`TradepostMailBodyTests`); the mail-flow ordering matches the
capture (BuyItem F502 → mail poll/preview/take F509–F557).

Two deliberate gaps: the mail-body item-name fragment is left empty (the per-item RDB name key cannot be synthesised
— the same client-RDB-by-id limit as conversation text), and the seller is not credited with the sale price.

## Sell into the AH + player SendMail

Decoded from `NecroL80_mage-necromancer_login_sell_item_on_ah-send_mail_with_attachment`.

**`TradepostSellItem` (C→S `0x200A`) — byte-exact (capture F375):**
```
[nBankItemSlot:u32][nPrice:u64]
```
F375 = slot 6 @ 1,000,000 tin. Lists one of the player's bank items (container 105) for sale: the server creates a
`TradepostListing` from the bank item's identity (seller = char name, the given price/qty + the origin bank slot).
The listed item stays in its bank slot — the client draws a "listed" overlay from three bank-slot stat updates
(price, timestamp, the listed flag; F376/F377) — and leaves the seller's bank only when someone buys it. There is no
TradepostInterface reply; the server pushes a `0x2005` wallet refresh with the balance unchanged (there is no listing
deposit or postage fee: the seller stays at 1,365,148 tin across the sell, send-mail, change-price and cancel
captures).

The listing is filed under the item's auction-house search category (`Items.SearchCategory`, e.g. 30030), the
item-type search id the client queries by, so an ordinary item-type search surfaces it; an item with no known category
falls back to its template id.

The seller addresses their own listing by the origin bank slot (not the saleId, which is for buying), so the listing
carries `TradepostListing.BankSlot`.

| dir | method | name | body | behaviour |
|-----|--------|------|------|-----------|
| C→S | `0x200A` | TradepostSellItem | `[nBankItemSlot:u32][nPrice:u64]` | list the bank item; it stays in the slot |
| C→S | `0x200B` | TradepostChangeItemPrice | `[nBankItemSlot:u32][nNewPrice:u64]` | reprice the seller's listing (F363: slot 6 → 5,000,000) |
| C→S | `0x200C` | TradepostCancelSellItem | `[nBankItemSlot:u32]` | delete the listing; item stays in the bank (F397: slot 6) |
| C→S | `0x2009` | CancelMail | `[mailId:u64]` | recall/delete a mail (F403) |

Handled in `TradepostService`; repo `CreateTradepostListing` / `GetTradepostListingBySellerSlot` /
`SetTradepostListingPrice`; golden tests `TradepostSellBodyTests`.

**`TradepostListing.ItemId` references a real `Item`** (FK to `Items.Id`, `OnDelete Restrict`), so a listing cannot
be filed under a non-existent item: a placeholder id shared by many listings makes the client collapse them into a
single row in every category. The listed instance's `Template` is a second FK to `Items` (`Restrict`, distinct from
the `ItemId` search key, which usually differs): the instance's per-template TEA key lives once in `Items` and the
codec reads it through the `TemplateItem` nav. `ItemHash` stays a row column (the AH wire hash is 0, not
`Items.Hash`). `MailAttachment` has the same `Template` FK → `Items`.

**`SendMail` (C→S `0x2002`)** — player→player mail with a bank-item attachment (capture F499). It creates a Mail to
the recipient; the attached item stays in the sender's bank with a mail-pending overlay (`0x201c` stat 0 = flags
`0x8001`, capture F500), and leaves the sender's bank only when the recipient takes it (the overlay clears if the
sender CancelMails it - F404 flag → `1`). Replies `DispatchSendMailResult`; an unknown recipient is rejected
(success=0). The mail remembers the sender's origin bank slot (`Mail.SenderBankSlot`). Grammar:
```
[u16+recipientName ascii][u16+bodyText ascii][money:u64][ListFrame(n)][bankItemSlot:u32]xn
```
F499 = to "DemoL80", body "Hello this is a test", money 0, attach bank slot 5 — the `0x7E2` word ahead of the slot is
the list frame over one attachment, and a mail sent with no attachment carries no slot. Reply
`DispatchSendMailResult` (S→C `0x2003`) = `[u8 success][u16+remoteformat XML]` (F500: success=1 + a result-message
remoteformat).
