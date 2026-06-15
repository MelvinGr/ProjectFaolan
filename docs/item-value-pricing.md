# Item value / vendor pricing (Gitea #137)

Goal: replace the provisional vendor pricing (`ShopService.VendorPriceTin = level×2×count`,
`VendorBuyPriceTin = TailLevel×2×count×4`) with the item's **real value** from the RDB item template, so
every item is priced — not just captured wares, and not from the (mostly-0) `Items.Level`.

## Confirmed: sell price = value-per-item × count

Ground truth from `Captures/account-faolan-character-necrol80-login-sell_items_to_trader-intract-with-auctionhouse`:

- The wallet update is `0x2005` but that opcode is **multiplexed** — filter by **`SenderClass 0xa9ecb95d`**
  (GameCharAgent `UpdateCash`), whose body is exactly 12 bytes `[u64 balanceTin][u32 reason]`. The other
  `0x2005` sizes (1272 / 2246 / 14500 B …) are a different class on the same 16-bit opcode.
- The reason=3 (gain) balance sequence is 19 / 42 / 134 / 526 / 573 / 594 / 658 → **deltas = sell prices
  19, 23, 92, 392, 47, 21, 64 tin** (the issue's anchors).
- Each sale's `0x2034` sell request (`UP`, 20 B) = `[handle][0x1e7e][container 0x68=backpack][0x8006f31][u32 slot]`
  — the last u32 is the inventory slot.
- slot → (template, count) comes from the FullChar snapshot (`0x737A6DF9`, frame 66) via the C#
  `InventorySnapshotInspector` (`ParseInventoryPacketBody` → `GetContainer(root, ContainerKind.Bag)` →
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

## The blocker: value is not a directly-stored template field

Searched each of the 7 decrypted templates (`/tmp/itpl/1000020_<template>.bin`, body = bytes **after** the
12-byte RDB-Id header) for its value-per-item as LE u32 **and** as protobuf varint, anywhere, at multipliers
x1 / x4 / x5 / x10 / x20 / x25 / x50 / x100: **no multiplier matches all 7** (only 3 of 7 even contain the raw
value as a u32, at inconsistent positions). So the sell value is not a trivially-stored field at a fixed
offset or simple scale.

`Items.SearchCategory` is already mapped (= `template[12..16]`, the first u32 of the body; validated 242/242
against captured AH listings). The value lives in one of the still-**unreversed binary fields** of the item
template (`RdbItemImporter` notes "reversing those fields into stat columns is the next step"; today it maps
only `SearchCategory` + the `0x21`-tagged name strings).

## Remaining work
1. Reverse the item-template tagged-field grammar enough to locate the value field — solve it against the
   oracle table above (parse the 7 templates' fields, find the field whose value, or a clean function of it,
   reproduces {19, 23, 92, 56, 47, 21, 64}).
2. Add `Item.Value`, populate it in `RdbItemImporter`, and replace `ShopService.VendorPriceTin` /
   `VendorBuyPriceTin` with `value × count` (vendor buy = a markup over that). Golden-test against the anchors.

## Reproduce
- RDB install: `/mnt/btrfs/@games/debian-installation/steamapps/common/Age of Conan/RDB/` (see the
  `client_rdb_install_path` agent memory).
- Dump decrypted templates: `dotnet run --project Faolan.Importer -- --captures /tmp/none --rdb "<RDB>" --rdb-items --dump-templates /tmp/itpl`
  (stop the server first — it holds `faolan.db`).
