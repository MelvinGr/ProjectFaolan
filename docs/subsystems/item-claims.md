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

Flow in the capture: the client sends `0x0007` (frame 398) → server answers `0x0001` with 19 entries
(frame 399, 2036 bytes) → the player redeems index 6 with `0x0003 00000006` (frame 461) → the item is
delivered by the normal live grant (`DispatchAddItem 0x2020`, frame 462). There is **no** `CoreClaimIIR`
reply to a plain item redeem — the client drops the row locally and the item just arrives.

**"Claim All" fires one `0x0003` per row using the ORIGINAL indices** (the second capture,
`…use-double-xp-potion`, sends 1..19 as a burst). So a redeem index must be resolved against the list *as it
was sent*, not a shrinking live query — else deleting each redeemed row shifts every later index and half the
batch misses. The server snapshots the sent order in `Session.ClaimSlots` and maps index→claim through it
(`GameServerListener.RedeemClaim`). Each redeem delivers exactly ONE item (no stat-26 count on the wire); a
multi-charge claim (Double XP) instead gets a `0x05` ack echoing the row with Amount−1 until exhausted.

### The 0x0001 list entry (106 bytes)

Built byte-exact by `CoreClaimListCodec` (proven by `CoreClaimListCodecTests.Build_MatchesCapturedList`
against frame 399):

```
u32   index          1-based slot (the redeem target)
u32   itemTemplateId
u32   itemTemplateId  (duplicated)
u64   level           the item's LEVEL (80, 5, 19, …) — NOT the stack count
byte  code[4]         4-char redemption code, e.g. "9CP4" (display-only)
byte  key[16]         the item's ProtoDecryptionKey (= Item.Key1..4, 4×u32 BE)
byte  key[16]         (duplicated)
byte  batchGuid[8]    per-account batch id — CONSTANT across every row in a response
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
(`GameServerListener.ItemKeyBytes`). `batchGuid` is display-only, derived deterministically per account.

**Field semantics (`level` vs `quantity`)**: the u64 is the item level, proven by the redeem — claiming
idx 6 (Level 5) delivered ONE Slavemaster's Staff (its `DispatchAddItem` carried no stat-26 stack count).
The tail is a charge pair `[remaining][max]`: the "Amount" column shows ∞ when remaining is 0 (single-use item
claims) and the number otherwise. The one type-4 row is "Double XP" — **Character**-bound, Amount 20 — a
multi-charge potion: each redeem hands over ONE and re-sends the row via the **sub 0x05** ack with
`[remaining−1][max]` (`SendRedeemAck`), dropping it only at 0.

### No hash on the wire

A claim row carries the item's **key but no hash** — and neither does the in-inventory item-use record
(`TemplateActionIIR 0x6C300937`, same key-only shape). Retail identifies these (often shop-only) items by
their key alone and renders them from the client RDB. So the ~10 templates that appear *only* in a claim
list import into `Items` with `Hash = 0` (`ItemHashHarvester`'s claim-list source harvests the key). Live
redeem works (the `0x2020` add path renders by template without a hash, via `GrantItemToBackpack(...,
allowHashless: true)`), but our login snapshot is hash-gated, so a redeemed hash-less item **drops on
relog**. The fix is render-by-key in the snapshot (matching the client), not hash harvesting — the hash does
not exist to harvest. Tracked as a follow-up.

### The 0x0009 "new claims" glow

`CoreClaimCodec.Build` (proven by `CoreClaimCodecTests`) is the short 14-byte push that lights the claims
button. Retail sends it at world entry **only when the account has unseen claims** — present in the Faolanx
login capture (new claims), absent in the NecroL80 capture (claims already viewed). The server gates it on the
`AccountClaim.IsNew` flag (`GameServerListener.SendNewClaimsGlowIfAny`), which is cleared the first time the
list is requested. Before this it was pushed unconditionally, so the button lit up for every account.

## Server implementation

- **Storage:** `AccountClaim` (account-scoped, an `Items` FK on `ItemTemplateId`; `Level` + `Quantity` +
  `ClaimType`). Age of Conan claims apply to the whole account, not one character; the constant wire
  `batchGuid` confirms the account batching.
- **List / redeem:** `GameServerListener.Claims.cs` — `HandleCoreClaim` dispatches the sub-opcode;
  `SendClaimList` builds the `0x0001` list (emitting each item's real key), snapshots the sent order into
  `Session.ClaimSlots`, and marks the account's claims seen; `RedeemClaim` resolves the index through that
  snapshot, grants one item (`GrantItemToBackpack(..., allowHashless: true)`), and drops the row.
- **Import:** the item **keys** are harvested into `Items` by the `items` module (`ItemHashHarvester`'s
  claim-list source); the `AccountClaim` rows are a **separate** opt-in `claims` module
  (`CaptureClaimImporter`, `--only claims`) onto the shared `captured` account.
- **Seeding:** the `.claim add <itemId> [count]` GM command (`.claim list` to inspect). Item-shop / reward
  grants that populate claims for real are a follow-up.

## Follow-ups

- **Render-by-key on relog (Gitea #359)** — the login snapshot is hash-gated, so a redeemed hash-less claim
  item (no hash exists in the protocol) drops on relog. Render it from the key like the client does.
- **Double XP "already held" rule (Gitea #360)** — the multi-charge decrement + `0x05` ack and the potion's
  on-use Double XP buff (`ItemUseType.XpBoost`) are implemented; the retail no-op-if-you-already-hold-one gate
  is not.
