# Item claims / Redeem window (CoreClaimIIR)

The client's **Claims / Redeem** window: a per-account list of items waiting to be collected (item-shop
purchases, veteran / pre-order rewards). All of it rides one IIR, **`CoreClaimIIR` (`0x06EC1255`)**, a
sub-opcode multiplexed message on the GameServer `0x2000` channel.

Ground truth: `Captures/NecroL80_mage-necromancer_login_redeem-claim.pcapng` — a login where the account has
19 pending claims, opens the window, and redeems one.

## Wire protocol

Every `CoreClaimIIR` body is `[opcode 0x06EC1255][0xC350 Character][handle][u16 sub][…]`. The `u16` after
the endpoint is a sub-message opcode (method index within the InventoryClaims IIR):

| sub | dir | meaning | payload |
| --- | --- | --- | --- |
| `0x0007` | c2s | "send me my claims" | none |
| `0x0001` | s2c | the claim list | `[u32 count]` + `count` × 106-byte entry |
| `0x0003` | c2s | redeem a claim | `[u32 index]` (1-based position in the last list) |
| `0x0005` | s2c | multi-charge redeem ack | one 106-byte entry with Amount−1 (Double XP only) |
| `0x0009` | s2c | "new claims available" glow | none |
| `0x000A` | c2s | list received (sent the moment the list lands) | none |

Flow in the capture: the client sends `0x0007` (frame 398) → server answers `0x0001` with 19 entries
(frame 399, 2036 bytes) → the player redeems index 6 with `0x0003 00000006` (frame 461) → the item is
delivered by the normal live grant (`DispatchAddItem 0x2020`, frame 462). There is **no** `CoreClaimIIR`
reply to a plain item redeem — the client drops the row locally and the item just arrives.

**"Claim All" fires one `0x0003` per row using the original indices** (the second capture,
`…use-double-xp-potion`, sends 1..19 as a burst). So a redeem index must be resolved against the list *as it
was sent*, not a shrinking live query — else deleting each redeemed row shifts every later index and half the
batch misses. The server snapshots the sent order in `Session.ClaimSlots` and maps index→claim through it
(`ClaimService.RedeemClaim`). Each redeem delivers exactly one item (no stat-26 count on the wire); a
multi-charge claim (Double XP) instead gets a `0x05` ack echoing the row with Amount−1 until exhausted.

### The 0x0001 list entry (106 bytes)

Built by `CoreClaimCodec`, decoded against frame 399:

```
u32   index          1-based slot (the redeem target)
u32   itemTemplateId
u32   itemTemplateId  (duplicated)
u64   level           the item's level (80, 5, 19, …) — not the stack count
byte  code[4]         4-char redemption code, e.g. "9CP4" (display-only)
byte  key[16]         the item's ProtoDecryptionKey (= Item.Key1..4, 4×u32 BE)
byte  key[16]         (duplicated)
byte  batchGuid[8]    per-account batch id — constant across every row in a response
byte  batchGuid[8]    (duplicated)
u32   type            6 = account-bound, 4 = character-bound (the "Bound To" column)
u32   quantity        the "Amount" column: 0 → ∞ (single-use), or the charge count (Double XP = 20)
u32   quantity        (duplicated)
u32   0
u32   0
u32   0
u32   1               fixed availability flag
u32   0
u16   0
```

**`key` is load-bearing, not opaque**: the client decrypts the item template from its own RDB with it, so
sending a wrong/synthetic key renders the row as "No name" (confirmed against Item 4208485 — its stored
`Key1..4` equals the captured token exactly). The server emits each claim's real `Items.Key1..4`
(`ClaimService.ItemKeyBytes`). `batchGuid` is display-only, derived deterministically per account.

**Field semantics (`level` vs `quantity`)**: the u64 is the item level, proven by the redeem — claiming
idx 6 (Level 5) delivered one Slavemaster's Staff (its `DispatchAddItem` carried no stat-26 stack count).
The tail is a charge pair `[remaining][max]`: the "Amount" column shows ∞ when remaining is 0 (single-use item
claims) and the number otherwise. The one type-4 row is "Double XP" — **Character**-bound, Amount 20 — a
multi-charge potion: each redeem hands over one and re-sends the row via the **sub 0x05** ack with
`[remaining−1][max]` (`SendRedeemAck`), dropping it only at 0.

### No hash on the wire — and why it does not matter

A claim row carries the item's **key but no hash** — and neither does the in-inventory item-use record
(`TemplateActionIIR 0x6C300937`, same key-only shape), an AH search listing (0 of 328) or a mail attachment
(0 of 2). Only messages describing a real item **instance** carry one: the world-entry snapshot (8,479 of
8,479), a shop window (2,734 of 2,734) and the live `0x2020` add.

A snapshot that rendered only `Hash != 0` would drop a hash-less item on relog, and re-pushing it as a live add at
every world entry would re-announce "You received [X]" on every map change. Two facts make it moot:

- **The hash is harvestable**, just not from the claim list — browsing any vendor that stocks the item is enough,
  since a shop window carries the full identity. 232 templates remain without one.
- **The remaining 232 do not need one.** The field is the client's `ACGHash`: a 4-character
  `[0-9A-Z_]` token whose bytes are all the client's reader validates — it compares the word against no
  expected value, and the token resolves to nothing in 28 GB of RDB content. Measured over the
  120 capture dumps it is per-template and perfectly stable (107,929 sightings, 0 templates carrying two),
  uniformly random at every position, and derivable from nothing. Confirmed in-client: an item given an
  invented in-charset token renders correctly, live **and** after a relog.

So `AcgHash.ForWire` mints a token when none was harvested, and `Items.Hash = 0` means "not harvested"
rather than "cannot be held". A minted token must be four `[0-9A-Z_]` characters — a random u32 fails the
client's charset check, which tests the validator rather than the identity.

#### Minting is disjoint from harvesting by construction

Every minted token carries an **underscore**, and **zero of the 4,615 harvested tokens contain one**. So a
minted token cannot collide with a retail one — not unlikely, impossible.

Minting inside the `[0-9A-Z]` space would be a lottery: the expected collisions per salt would be
`529×4615/36⁴ + 529²/(2×36⁴)` = **1.45 + 0.08 = 1.54**, so a salt would be clean ~22% of the time (measured: 90 of
400 consecutive salts) — and the dominant term *grows with every harvest*. What is left is the minted-vs-minted
term alone in the 186,624-token `_`-bearing space: **0.75**, ~47% clean, and it **shrinks** as harvests cover
more templates, because each newly harvested one stops being minted for. `--report acg-token-check` prints
exactly that arithmetic.

**`_` is the byte the in-client check did not exercise** (that run used `ZZZZ`/`ZZZY`, both inside `[0-9A-Z]`).
The client's own validator, read out of the binary, covers it. `FUN_005d7190` stores the four bytes as a `u32` and walks indices 0..3 applying **one predicate
identically at every index**:

```c
('/' < c && c < ':') || ('@' < c && c < '[') || c == '_'      // 0-9, A-Z, _
```

falling out of the loop into `Read invalid hash "…" from stream.`. **There is no per-position branch**, so `_`
is legal in any of the four including the first — which is the specific risk the gate existed for. And the
string `ACGHash` appears in that one function and nowhere else in the image: the token is never decoded into
digits, only stored and compared as an opaque word, so no consumer can treat `_` arithmetically at all.

A minted token is also **identifiable** (`AcgHash.IsMinted`), so a synthesized token can be told from a
harvested one after the fact. Open work tracked in Gitea #899.

### The 0x0009 "new claims" glow

`CoreClaimCodec.Build(new CoreClaimNewClaimsIIR(...))` (proven by `CoreClaimCodecTests`) is the short 14-byte push that lights the claims
button. Retail sends it at world entry **only when the account has unseen claims** — present in the Faolanx
login capture (new claims), absent in the NecroL80 capture (claims already viewed). The server gates it on a
single per-account flag `Account.ClaimsAvailable` (`ClaimService.SendNewClaimsGlowIfAny`) — set true
when a claim is added, cleared the first time the Claims window is opened. Pushed unconditionally, it would light
the button for every account.

## Server implementation

- **Storage:** `AccountClaim` (account-scoped, an `Items` FK on `ItemTemplateId`; `Level` + `Quantity` +
  `ClaimType`). Age of Conan claims apply to the whole account, not one character; the constant wire
  `batchGuid` confirms the account batching.
- **List / redeem:** `Faolan/GameServer/Economy/ClaimService.cs` — `HandleCoreClaim` dispatches the sub-opcode;
  `SendClaimList` builds the `0x0001` list (emitting each item's real key), snapshots the sent order into
  `Session.ClaimSlots`, and marks the account's claims seen; `RedeemClaim` resolves the index through that
  snapshot, grants one item (`GrantItemToBackpack`), and drops the row.
- **Import:** the item **keys** are harvested into `Items` by the `items` module (`ItemHashHarvester`'s
  claim-list source); the `AccountClaim` rows are a **separate** opt-in `claims` module
  (`CaptureClaimImporter`, `--only claims`) onto the shared `captured` account.
- **Seeding:** the `.claim add <itemId> [count]` GM command (`.claim list` to inspect). Nothing else populates
  claims (no item-shop or reward grant).
- **Double XP:** the multi-charge decrement + `0x05` ack and the potion's on-use buff (data-driven through
  `Items.OnUseSpell`, `ItemUseService`) are implemented; retail's no-op-if-you-already-hold-one gate is not. Open
  work tracked in Gitea #360.
