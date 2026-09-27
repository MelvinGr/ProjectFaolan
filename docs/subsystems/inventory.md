# Inventory: the FullChar snapshot, delivery model, and equip moves

Verified end-to-end in-client: the bag renders, the equipment doll renders, and equip/unequip persists and
survives relog.

## The snapshot (0x737A6DF9 FullPlayerCharacterIIR)

The body is read by `n3InfoItemRemote::Construct` (decompiled from `AgeOfConan.exe` — see `docs/re/`),
all **big-endian**:

```
[u32 KEY = subop = 0x737A6DF9][u32 identity.type = 50000 (0xC350)][u32 identity.instance]
[u8 flag][u32 BE blobLen][protobuf blob = blobLen bytes][u32 BE version = 0x18][tails]
```

- **The protobuf root is length-prefixed by a 4-byte big-endian length** (`FUN_00cbfb80` reads exactly
  `blobLen` bytes, then `ReadSubClass` reads the `0x18` version). The blob **is** the whole FullCharacterIIR
  root, so the prefix is exactly 5 bytes and **`blobLen = rootLen`**. A regenerated root needs that BE32 length
  rewritten (`FullPlayerCharacterCodec.Build` writes it; `SnapshotBlobLengthTests`) or the client reads the version
  from the wrong place and rejects the FullChar (`CoreFullPlayerCharacterIIR::ReadSubClass() Wrong IIR_t version
  ...`). The captured beach prefix `00 00 00 02 ae` is `[00 = flag][00 00 02 ae = blobLen 686]`.
  Two readings fit the beach captures and are wrong: a `varint = 4483 + rootLength` prefix (a coincidence of two
  near-equal-length captures), and a constant 3-byte "field5" `28 81 06` before a `root = blobLen - 3`. Those three
  bytes are the root's own first field, `subscription_flags` (field 5, varint 769), 3 bytes long only on the L1
  beach captures; a character with `character_tag` (field 1, packed) leads with that instead, so a fixed skip lands
  mid-varint on every level-80 snapshot.
- Root = `ProtoFullCharacterIIR` (recovered schema, byte-exact round-trip on the captured snapshots):
  field 6 × N = the containers (Equipped 101 / Bag 104 / Resource 112 / Quest 113), then fields 7, 8.
- Tails = the 9282-byte record/XML region starting `00 00 00 18` (subclass version 24). The live world-entry
  path builds them from scratch with this character's live level/vitals/XP via
  `SnapshotTails.FromScratch`, proven byte-exact against the captured `BeachTailsTemplate.bin`
  oracle (golden tests `BeachSnapshotConstructionTests`).

Containers are built wholesale from the DB (`FullCharacterRoots.Containers`: the character's `ItemInstance`
rows — ALL of them, worn included; entry = `SnapshotItems.ListEntry`, identity = the `Item.Key` (Data1..4) quad
+ the wire token `AcgHash.ForWire(Item.Id, Item.Hash)`). Every item renders.

## An item is a row, not an address

`ItemInstance` is one item: the template it copies, where it currently sits (`Container` + `Slot`, with a
UNIQUE index on `(OwnerCharacterId, Container, Slot)`), and every per-instance fact — the stack `Count`, the
`CreatorName`, and the gems in `ItemInstanceSocket`. A worn item is an instance in container `Equipped` (101)
whose `Slot` is the wire equip slot id, vanity slots (1000 + the base slot) included.

- **A move is an update of two columns on one row** (`IDatabaseRepository.MoveCharacterItem`), so the instance
  and everything hanging off it survives by construction. A swap is three statements in one transaction: two
  rows trading `(container, slot)` is a cycle under the unique index, so the displaced one is parked at a
  negative slot in between.
- **Only `SetCharacterItemSlot` mints**, which is what lets `InventoryGrantChokePointTests` gate deliveries on
  one method: a move or a count change cannot introduce an item, so neither can newly tick a collect goal.
- **Splitting a stack mints a new instance.** A stack of 20 Copper is ONE row with `Count = 20`.
- `ItemInstance.Id` is a store-generated identity — correct here, despite the no-surrogate model convention,
  because that convention is about tables the importer delete+reinserts. This is player state, excluded from
  `faolan.sql` like `Characters`. The id is also the low half of the wire's `item_id` under the retail
  `0x20D82B63` epoch, which is what retail's own "epoch + a server allocation counter" shape wants.
- **`OwnerCharacterId` is nullable** — the guild bank vault sets `OwnerGuildId` instead (below), and `Container`
  is the discriminator. Mail attachments and AH listings keep their own tables; a mail attachment points at the row
  it escrows (below).

An item keyed as an address (a per-slot column, or `(CharacterId, Container, Slot)`) would have to carry every
per-instance property by hand through every move, and a worn item would have nowhere to keep one at all.
`Character.VanityShownMask` is not a property of any item, so it lives on the character.

`Items.Hash` is the **harvested** token and is legitimately `0` for 232 of the 4,225 shipped items — those no
capture ever showed entering an inventory or a shop window. The snapshot does not drop them (rendering only
`Hash != 0` makes claims undeliverable and AH purchases live-only); `AcgHash` mints a token for them instead. See [item-claims.md](item-claims.md) and `Faolan.Core/Crypt/AcgHash.cs`.

### The crafter — `creator_name`, not a stat

A player-crafted item shows the client's "Created by &lt;name&gt;" tooltip line, and the only thing that feeds it
is **`ProtoInventoryGameEntry.creator_name` (field 5, a plain Latin1 string on the entry itself)**. No stat is
involved — in particular it is not the `ItemOrigin` stat (0x554), which is why the whole crafting stat block
(0x552–0x55E) is absent from every captured entry. [V2: NecroL80_mage-necromancer_login_gameplay_annotated_5.tshark.json fr17401]

It is present only on player-made items: 619 captured inventory entries carry one (5 distinct crafters), and
every other entry ends at the item block with no field-5 tag at all. So an empty name must emit **nothing**
rather than an empty string. It rides both shapes — the world-entry snapshot (348 sightings in `Bag`, 271 in
`Equipped`) and the live `DispatchAddItem`.

Persisted as `ItemInstance.CreatorName`: it is a per-instance fact, so it cannot live on `Item`. A move keeps
it without anyone carrying it (the row is updated, not re-created); a mint assigns it explicitly, defaulting to
null, so an item arriving in a cell a crafted item used to occupy is not itself player-made. The `Equipped`
sightings persist too — a worn item is an instance like any other.

## Delivery model

1. **The world-entry snapshot is the inventory delivery** - it feeds both the bag window and the equipment
   doll, and nothing re-feeds them later. `SendBurstlessWorldEntry` builds the snapshot from scratch — the
   player's DB containers become the FullCharacterIIR root (`FullCharacterRoots.ForCharacter` +
   `FullCharacterRoots.Containers`) — and `FullPlayerCharacterCodec.Build` writes the BE32 blob length to match.
2. **Container mutations do not stall world-load or chat** once the ContentMirror serves item resources: a stall
   there is the client thrashing on item resources it cannot fetch from the dead retail CDN.
3. **A second 0x737A6DF9 for the same identity is dead on arrival.** The client either rejects it
   (ReadSubClass error - even an empty rebuild) or constructs it and never re-reads it into the UI — so
   re-sending the whole snapshot is not how you deliver a mid-session item. The live add is a per-item packet —
   `DispatchAddItem` (s2c 0x2020), decoded + byte-exact + in-client verified (`GrantItemToBackpack`;
   `DispatchAddItemTests`); `.additem` / `add_item` both persist the row and push the live add (no relog needed).
   The live remove is `DispatchRemoveItem` (s2c 0x201F): byte-exact builder (`RemoveItemCodec`/`SendRemoveItem`,
   body `[container ID32][u32 pos][u32 amount]`, `DispatchRemoveItemTests`); `RemoveItemFromBackpack` clears the
   persisted cell and pushes the live remove, reached from `/inventory delete <slot>`.
4. **Bag-poison items**: an item only ever captured worn (never inside an inventory container) can render
   the whole bag empty when placed in a bag cell (repro: 13826 Combatant's Hauberk; client logs
   VisualMeshLoader 1010001 misses). Likely missing local bag/icon resources.
5. **Wire health matters more than content**: a `Socket.Send/SendAsync` partial write truncates the ~10 KB
   snapshot mid-frame (send buffer full right after the world burst), making byte-identical snapshots fail
   "sometimes". Sends loop to completion (`NetworkClient`).

## Equip/unequip moves (Interact 0x09) - decoded from capture

Capture: `Stihetfv_soldier-conqueror_login_combat_armed-and-unarmed`. Golden tests:
`BehaviorAcksTests`.

- Request args protobuf: `{TypeId, Mode, Category:10, Field6:0}`. **Mode is the client's monotonic
  per-session action counter** (shared across all BCC interactions), not a direction. **TypeId selects the
  drag's window pair**: 42010/42011 (one gesture family), 41842/41843 (the backpack-window family) - same
  tail grammar.
- Into-slot tails: `[srcBagCell][destEquipSlot][itemTemplate][itemTemplate][0][flags]`;
  out-of-slot tails: `[srcEquipSlot][destBagCell][0...]`.
- Server reply = the behavior echo-ack (request echoed, seq+1, field3 = the request's Mode spliced into the
  args) with the tail's zero padding replaced by the identity block: `[Item.Data1..4 quad] x2 +
  [B2 90 80 5C 29 E6 27 CA] x2` on equip, the null filler x6 on unequip - plus a 0x201C record
  (`prefix 0x65, state = destSlot` on equip; `0x68, 0` on unequip).
- Persistence: `Inventory/EquipmentService.cs` (equip writes the slot + consumes the bag row; unequip parks the
  item at the exact bag cell).

Slot ids (equipped container `pos` = `CharacterEquipmentSlot` = the worn instance's `Slot`): chest 5, main hand
6, ring 9, legs 16, wrist 18, second weapon/sheath 0x14 - verified against the captured container.

### Which slot an item goes into - the client says

Three item-template property fields answer it, all three named by the client's own stat-name table
(`docs/re/aoc-client-stat-names.tsv`) and all three carried by **every one of the 5,071 decrypted templates**.
Imported by `RdbItemImporter` onto `Item`, read by `EquipSlotResolver`:

| field | stat | column | meaning |
|---:|---:|---|---|
| 298 | `0x12A` `Placement` | `Item.Placement` | bitmask of `CharacterEquipmentSlot` ids the item may occupy. **0 = not equippable** |
| 88 | `0x58` `DefaultPos` | `Item.DefaultPos` | the preferred slot when the mask offers several |
| 148 | `0x94` `OccupySlots` | `Item.OccupySlots` | every slot the item consumes once worn |

**Validated against every worn item the corpus records** — 72 template/slot pairs from `ItemInstances` container
101, with no contradiction:

- a ring is `1536` = bits 9|10 (either finger), and the corpus has one template worn at 9 on one character and
  10 on another;
- a one-hander is `320` = bits 6|8 (either hand);
- a two-hander is `64` = bit 6 alone **plus** `OccupySlots` `256` = bit 8, i.e. the off hand goes too. 280
  templates have that shape and every one is in a documented two-handed weapon class (30012 2h edge/blunt,
  30014 2h blunt, 30016 polearm, 30044, 30019 thrown);
- `DefaultPos` is **not** derivable from `Placement`: 45 off-hand focus items (category 30009 — tomes, folios,
  trinkets) carry the one-hander mask `320` and default to `8`.

**Bits 6 and 8 mean "a main hand" and "an off hand", not slots 6 and 8.** Slots 20/21 are weapon set 2 and are
set-neutral — either set holds any weapons — so which set a hand bit lands in is the wearer's choice, not
a property of the item. That single decision is the only thing `EquipSlotResolver` adds to the template data; it
fills set 1 first and falls through to set 2. Open work tracked in Gitea #400 (the active weapon set).

4,022 of the 5,071 templates are equippable. `Placement` `0` on the other 1,049 is a statement, not a gap: they
are keys, notes, pelts and crates.

**Slot 23 = `Carried`.** Six templates place there (`Placement` = `1<<23`,
`DefaultPos` = 23) and they are exactly the things a character visibly carries: *Bundle of Wood*, *Baracha Basket
of Fruit*, *Battle Plan Fragment I*, *Letter to Hakon Wolftamer*, and two whose own asset names end in
`_carryable`. No capture shows one worn — the corpus runs no carry quest — so it is named from the client's data,
as slot 26 (`ResourceBag`) was before a live equip confirmed it. Independent corroboration of the whole
reading: `Placement` bit 26 appears on exactly the two resource bags, and 26 was recovered separately, off the
wire.

Bulk moves ride this: `EquipmentService.EquipAll` walks the backpack filling free slots (it does not judge which
of two swords is better — the template data does not answer that), `UnequipAll` walks the worn set into the
backpack and stops cleanly when the bag fills. Both persist and return what moved; the caller sends a
`DispatchRemoveItem` + `DispatchAddItem` pair per move, which is what a server-driven inventory change looks
like on the wire (a client-driven equip needs neither — the client is the one that moved it). GM verbs:
`.inventory equipall` / `.inventory unequipall`. Open work tracked in Gitea #916.

## Sockets (gems)

A socket lives on the wire in two forms, and **neither carries a stat value** — the socket names the gem by
template and the client derives its effect from the gem's own template:

- **persistent, twice over.** `ProtoInventoryGameEntry.slot_map` (field 4), repeated
  `{ slot, item: ProtoAcgItem }` — the login snapshot restates every socketed item's full gem list, which is how a
  gemmed item survives a relog. Measured over the 898 blocks in the corpus slice: the gem's `level` is its own
  `Items.Level` and its `hash` is 0.
  **The same socket also rides the entry's `stat_list`** as the pair `ItemSlot<N>ACG1` / `ItemSlot<N>ACG2`
  (stat ids `0x2A3 + 2N` / `0x2A4 + 2N`) — the client's own names for a socket slot's two ACG ids, which are the
  gem's `template1`/`template2`. The two forms **agree 898/898**, with zero disagreements and zero cases of one
  present without the other, so they are not alternatives: retail sends both, every time, and so do we.
  The stat table names four slots (`ItemSlot1`..`ItemSlot4`), so the corpus' socket indices 0/1/2 are a **capture**
  limit rather than the game's.
- **live**: `DispatchAddToItemSlot` (GameCharInterface 0x1D) carries the same pair for one socket, followed by
  an ordinary `DispatchRemoveItem` for the gem leaving the bag. The socketed item's container entry is not
  re-sent — the client updates it in place.

### Socketing a gem

The c2s trigger is BCC behaviour **`0x27` `Tradeskill`** — the client's own registered command class, carrying
**two** operations discriminated by the tail's leading byte:

| byte | operation | payload |
|---|---|---|
| `0x01` | craft from a recipe | `[u32 recipeId][u32 quantity]` |
| `0x02` | socket a gem | `[u32 container][u32 charInstance][u32 slot]` ×2 — **target first, gem second** |

The args do not discriminate: `bcc_action_id` is 49340 on both and only `commandqueue_client_id` differs.
That matters because `MoveEquippedToFromInventory` *does* split its directions on the action id, so the obvious
place to look is the wrong one. The address order is settled by the replies, not by reading order —
`DispatchAddToItemSlot` names the first, `DispatchRemoveItem` the second.

`TradeskillCommandCodec` decodes the tail (reached through `BehaviorTailScan.Tradeskill`, the one seam for tail
reads); `AddToItemSlotCodec` writes the 84-byte reply, whose trailing 68 bytes are a `ProtoAcgItem`'s fields with
the protobuf framing taken off.

**The rule**, applied by `TradeskillService.Socket` before anything is written, because socketing **consumes** the
gem: the item must declare `Item.GemSlots > 0` (`NumGemSlots`, template field 114 — 647 templates do), and the gem
goes in the first free index below that count. A refusal is **silent**, like a refused craft — retail answers a
successful tradeskill and says nothing otherwise.

An item may already hold **more** gems than it declares: retail reduced some slot counts in a later patch and
the old items kept their gems, which the client itself calls `GemOverflow` (it ships a token, an explainer and a
clickable link for the state). So the count bounds what may be **added** and deliberately does not judge what is
already there.

`[V0: the gem/slot COLOUR match - the client asserts on "Invalid gem slot colour" and the stat table names
GemSlot1Color..GemSlot4Color (694-697) against the gem's own GemColor (698), but neither is stored]`
Open work tracked in Gitea #754.

Stored as `ItemInstanceSocket` `(ItemInstanceId, SocketIndex) -> GemItemId`, and emitted back by
`SnapshotItems`. The socket contents are the truth; the stats are derived — which is why sockets are their own
table rather than rows in a per-instance stat table.

**A gem in a worn item grants its stats.** Because a socket carries no stat value, the gem's effect is just
its own template's modifier list, so `GetEquippedStatItems` returns the socketed gems alongside the items holding
them and the ordinary aggregation does the rest — **one entry per socket**, so two of the same gem is that bonus
twice (de-duplicating would halve it, as it would two identical worn items). Only gems in equipped items count;
one in a bag item does not. `CaptureSocketImporter` (importer module
`character-sockets`) harvests them onto characters that already exist, since the `characters` module is
insert-only.

`SnapshotItems` must populate `slot_map`; an empty one silently strips every gemmed item of its gems at world
entry.

### The item's two templates

`ProtoAcgItem` carries `template1`/`template2` with a key each, and they are the **low and high endpoints of a
level range** — `AcgItemData_t::GetInterpolatedItem` takes both plus the entry's own `level` and works out where
between them this copy sits. Measured over 13,368 captured ProtoAcgItems (344 distinct pairs), the three that
differ differ in nothing but level fields, same display name and all:

| pair | name | differs | the entry's `level` |
|---|---|---|---|
| 3904842 → 3904843 | Alchemist | `54`: 40 → 49 | 44 — inside |
| 3976768 → 3976769 | Cabochon Azurite | `54`/`203`/`204`: 50 → 80 | 80 — the top |
| 3983088 → 3983089 | Sanguine Radiant Sapphire | `54`/`203`/`204`: 75 → 80 | 75 — the bottom |

Stored as `Item.TemplateId2` + `Item.PairKey`, and emitted by `SnapshotItems.Identity`. The key is not optional:
the second template is TEA-encrypted under its **own** key, so sending `template2` without it would be worse than
repeating `template1` — the client would hold a template it cannot read. Null template2 and an all-zero `PairKey`
mean the same thing, and neither may exist without the other (`ItemTemplatePairTests`).

It is **not derivable**. `template2 == template1 + 1` on all three, but 341 of the 344 pairs have
`template2 == template1`, so `+1` would be wrong far more often than right. Across all 344: `key3 == key1`
344/344 and `template3 == 0` on every one, so the third slot is unused rather than undecoded. `--report acg-pair`
re-derives it.

### Mail attachments and auction-house escrow

An attachment that carries only a template id — minting a fresh item on take and deleting the seller's row — would
deliver **a gemless, crafterless copy** of an auction-house sale. `MailAttachment.EscrowedInstanceId` points at the
actual **row** instead, and a take hands it over through the same choke point a trade uses. Where the item waits differs by how the mail arose, and both follow
retail:

| | where the item sits while the mail is unread |
|---|---|
| **player mail** | the sender's bank slot — it does not leave on send, and cancelling just clears the pending overlay |
| **AH purchase** | the buyer's `Mail` container (**114**), moved off the seller the instant the sale completes — which is what stops the seller taking it back out of their own bank |

Container 114 is retail's: it registers that cell for the buyer immediately after `BuyItem`, before the mail is
read.

**`EscrowedInstanceId` is nullable and both cases are real.** An importer-seeded listing has no seller character
and never had an item row, so its purchase mail keeps a null escrow and still mints from the template. Everything a
player causes carries a row. The `Template`/`Quantity`/`ItemHash` columns stay beside it because they are what the
wire entry carries, they are the only source when the escrow is null, and `ItemHash` genuinely is not the template's
(an AH-delivered item sends 0).

`ItemLevel` is **not** among them. The level is strictly per-template — across 8,644 captured inventory sightings not
one template carries two different levels — so a per-instance copy would store nothing `Items.Level` does not, on
either `MailAttachment` or `TradepostListing`. Both codecs read it through the joined row they already `Include` for
the item's TEA key (a transient row must carry the level on its navigation; the byte-exact goldens on both catch it).
`MailAttachments` is keyed `(MailId, ItemInstanceId)` — the wire's own identity for the block.

**Cancelling a mail returns the escrow first and is refused if it cannot** — a bought item lives in the buyer's
mail container until taken, so deleting the mail on top of it would strand a paid-for row nothing can reach. And
the take does not clear the sender's bank cell when the row was handed over: the transfer already moved that row,
so clearing by `(owner, container, slot)` would match whatever the sender has since put there.

Gated by `MailEscrowTests`.

### Loot bags

Not part of this at all. `MapLootEntries` is `(MapId,
ItemTemplateId)` — a per-map loot **pool**, i.e. content data. A looted item has no per-instance history to
preserve because it does not exist until it is granted.

### The guild bank vault

A guild's shared item storage is **container kind 109**, and it is the one container on the wire whose
`inventory_id.instance` is a **guild id** rather than a character id.

Stored in `ItemInstances` like every other item, with `OwnerGuildId` instead of `OwnerCharacterId`. **Exactly one of
the two is set**, enforced by a
`CK_ItemInstance_OneOwner` check constraint, and the guild's items are `Restrict` rather than `Cascade`: disbanding
a guild with a stocked vault should fail loudly, not silently destroy everyone's deposits.

A deposit **transfers ownership** — the depositor stops owning it, which is what makes it survive them leaving the
guild. It is still an `UPDATE` of the same row, so the crafter, the stack and the socketed gems go in and come back
out intact. We do not record who deposited an item: retail has a deposit log, but nothing in the capture carries
one and inventing a column would model a feature we have not seen.

| | |
|---|---|
| open | one banker interaction pushes the personal bank's slot registrations, `DispatchOpenTradepost` **and** `DispatchGuildBankData` in the same instant. There is no `OpenGuildBank` request on the wire at all |
| move | an **ordinary `MoveInventoryItemIIR`** with the vault on one end → `DispatchMoveInventoryItemToOrFromGuildBank`, the same six values echoed back raw |

**The move is not a message of its own.** `0x2A8A1B49` looks novel in a dump and is just the inventory drag the
client sends for any container-to-container move, so the listener routes on the **container**, not on a second
opcode. The deposit and withdraw are that one message with its halves swapped — no direction flag — and the
`DispatchMoveInventoryItemToGuildBank` (0x21) / `...FromGuildBank` (0x22) pair the method table names never appears.

The vault instance is **client-supplied**, so `GuildBankService` checks it against the character's own guild —
without that a member could name another guild's vault and help themselves. An occupied destination is refused
rather than swapped: both captured moves land in a free cell, so what retail does otherwise is unobserved and a
wrong guess costs somebody else's deposit.

The money half is separate and lives on the CS tier (`GuildService`), because a balance change is pushed to every
online member. Gated by `GuildBankVaultTests`. Open work tracked in Gitea #654.

### Binding

`Flags` — the entry's **stat 0** — carries the bound state in **bit 26**, and the wire was caught doing it:

```
09:46:44  SetItemStat  bag(104) slot 8       stat 0 = 0x00000001    "unbound: [Imbued Frayed Circlet]"
09:47:00  SetItemStat  equipped(101) slot 2  stat 0 = 0x04000001    "just eqpuied item, it is now bound"
```

`[V2: demol80-demonologist_login_gameplay_annotated_3.tshark.json 09:46:44 and 09:47:00]` — the player narrating
both halves in `/say`. So binding is the server re-sending that cell's `Flags` with bit 26 set; nothing else moves.

Both rules come from the RDB item template, and both are derived rather than stored as conclusions:

| | derivation | stored |
|---|---|---|
| `Item.BindsOnPickup` | `Flags` bit 26 — the template mints its copies already bound | nothing; `Flags` holds it and ships verbatim |
| `Item.BindsOnEquip` | `MoreFlags & 0x20 \|\| (Quality > 1 && Can & 4)` | `MoreFlags` (field 177), `Can` (field 30) |

1,267 templates bind on pickup, 2,035 on equip. `ItemInstance.Bound` is the per-instance half and is one bit —
only bind-on-equip needs it, since a bind-on-pickup item's own template word already carries the bit. It is set by
`EquipmentService.PersistEquip` and reaches the wire through the instance overloads of `SnapshotItems.ListEntry`
/ `LiveAddEntry` only: a caller holding no instance is describing an item nobody owns, which is never bound.

Binding is one-way — nothing unbinds an item in the corpus or the client's method table. Not enforced: a bound
item can still be traded, mailed and AH-sold. Open work tracked in Gitea #756. Full derivation in
[`../rdb/item-template-stats.md`](../rdb/item-template-stats.md); measured by `--report item-binding`.
