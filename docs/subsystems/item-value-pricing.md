# Item value / vendor pricing (Gitea #137)

Goal: replace the provisional vendor pricing (`ShopService.VendorPriceTin = level×2×count`,
`VendorBuyPriceTin = TailLevel×2×count×4`) with the item's **real value** from the RDB item template, so
every item is priced — not just captured wares, and not from the (mostly-0) `Items.Level`.

## Confirmed: sell price = value-per-item × count

Ground truth from `Captures/NecroL80_mage-necromancer_login_sell_items_to_trader-intract-with-auctionhouse`:

- The wallet update is `0x2005` but that opcode is **multiplexed** — filter by **`SenderClass 0xa9ecb95d`**
  (GameCharAgent `UpdateCash`), whose body is exactly 12 bytes `[u64 balanceTin][u32 reason]`. The other
  `0x2005` sizes (1272 / 2246 / 14500 B …) are a different class on the same 16-bit opcode.
- The reason=3 (gain) balance sequence is 19 / 42 / 134 / 526 / 573 / 594 / 658 → **deltas = sell prices
  19, 23, 92, 392, 47, 21, 64 tin** (the issue's anchors).
- Each sale's `0x2034` sell request (`UP`, 20 B) = `[0xc75b shop-type][shopId 0x1e7e][container 0x68=backpack][0x8006f31 seller][u32 slot]`
  — the last u32 is the inventory slot (verified against frames 1282/1291/1301/1325/1339/1365/1369, body e.g. `0000c75b00001e7e0000006808006f3100000005`).
- slot → (template, count) comes from the FullChar snapshot (`0x737A6DF9`, frame 66) via the C#
  `FullCharSnapshotCodec` (`ParseInventoryPacketBody` → `GetContainer(root, ContainerKind.Bag)` →
  `GetEntrySlot/GetEntryTemplateId/GetEntryCount`). NecroL80 isn't in the DB inventory, so it must be read
  from the capture.

Combined oracle (`...sell_items_to_trader...`), **value-per-item = delta / count**:

| backpack slot | template | count | wallet delta (tin) | value/item |
|---|---|---|---|---|
| 5  | 3886819 | 1 | 19  | 19 |
| 8  | 3887406 | 1 | 23  | 23 |
| 7  | 3887364 | 1 | 92  | 92 |
| 9  | 3917245 | 7 | 392 | **56** |
| 12 | 3514741 | 1 | 47  | 47 |
| 6  | 3724525 | 1 | 21  | 21 |
| 4  | 3719048 | 1 | 64  | 64 |

Slot 9 (count 7, total 392 → 56/item) is the proof that price scales with count.

## SOLVED 2026-06-17: value = property-table field 74

The earlier "no multiplier matches" search failed because it scanned **raw offsets**. The body is an RDB
**property table** — a run of `[fieldId u32][value u32]` LE pairs with strictly-decreasing fieldId after the
header (same grammar as `DispatchAddItemTests`). The value is **field 74**, and the
**vendor sell-per-unit = round(value / 10)** (integer `(value+5)/10`):

| template | field 74 | sell/item |
|---|---|---|
| 3886819 | 190 | 19 |
| 3887406 | 230 | 23 |
| 3887364 | 920 | 92 |
| 3917245 | 560 | 56 (×7 = 392) |
| 3724525 | 206 | 21 |
| 3719048 | 637 | 64 |
| 3514741 | **0** | — (no field 74 value → **unsellable**, the Totem of Origins) |

`Items.SearchCategory` (= `template[12..16]`) was already mapped; field 74 is the second mapped property.

## Implemented
- `Item.Value` column (part of the squashed `InitialCreate` migration; the standalone `AddItemValue`
  migration no longer exists); `RdbItemImporter.TryReadPropertyField` extracts field 74
  (1677/2453 keyed items populated).
- `ShopService`: `VendorPriceTin(value, count) = round(value/10)×count`; `VendorBuyPriceTin = ×4 markup`;
  **value 0 ⇒ unsellable** (`HandleSellItemToShop` refuses). Golden tests = the anchors above (`ShopTests`).
- `stat 356` (in the shop window) is a CONSTANT `0x394E0034`, **not** a price.

## Remaining (open)
The BUY markup (×4) has no captured anchor — it's a stand-in over the now-real sell value.

## Reproduce
- RDB install: the client's `RDB/` folder (`<rdb-dir>`; the concrete path is in the
  `client_rdb_install_path` agent memory).
- Dump decrypted templates: `dotnet run --project Faolan.Importer -- --only rdb-items --rdb "<RDB>" --dump-templates /tmp/itpl`
  (`rdb-items` is an opt-in module; `--dump-templates` is its disk-write sub-option — stop the server first,
  it holds `faolan.db`).
