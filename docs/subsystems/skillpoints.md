# Skillpoints

A character earns a pool of **skill points to distribute** (`SkillPoints`, stat `0x0F3`) and moves them into the
trainable skills. This is a distinct system from feats (a separate point economy, a separate window) and from
tradeskill professions (learned from a trainer, ranked by a feat gate) — it shares only the stat-id space.

Decoded from the four annotated spends in
`NecroL80_mage-necromancer_login_gameplay_annotated_3`, each of which the player narrated in `/say` as it happened.

## The spend — c2s `SkillIIR` (`0xD558FF16`)

```
[u32 0xD558FF16][u32 entityClass][u32 handle][u8 env][ListFrame(n)]( [u32 statId][i32 delta] )xn
```

The head is the same base record `StatIIR` carries (class + handle + the base byte); the tail is the `0x3F1`-framed
`(id, value)` list the client's own reader takes — `FUN_007a9a40` → `FUN_004d5900`, which asserts
`word % 0x3F1 == 0` and reads `count` pairs into a map. `env` is `0x00` on every captured spend.

The list IS the transfer: one pair debits the pool, the rest credit skills, and the deltas sum to zero.

```
00000021 d558ff16 0000c350 0802e5d4 00 00000bd3 000000f3 ffffffff 0000043d 00000001
         ^IIR     ^class   ^handle  ^env ^2 elems ^0x0F3   ^-1      ^0x43D   ^+1
```

| time | skill stat | annotation |
|---|---|---|
| 17:44:45 | `0x43D` `RunSpeed` | *"just put 1 skillpoint in run speed"* |
| 17:45:05 | `0x332` `Climbing` | *"just put 1 skilpoint in climbing"* |
| 17:45:18 | `0x33C` `Hiding` | *"just put 1 skilpoint in hiding"* |
| 17:45:30 | `0x33E` `Perception` | *"just put 1 skilpoint in perception"* |

Four for four, in order. [V3: SkillPointTests.ParseCapturedSpend_IsAConservingTransferOutOfThePool]

**The `0x3F1` trap.** These bodies read naturally as
`[u32 0x0000000B][u8 0xD3][pairs]` — two plausible constants that are really **one misaligned word**. Split
correctly, the base record's `env` byte takes the third byte of the apparent zero-run and what is left is
`0x00000BD3 = 3 × 0x3F1` = a two-element list. Both readings consume the same 37 bytes and both "work" on all four
captures; only checking the word against `ListFrame` tells them apart.

This is also the `AdjustSkill(int i_nSkill, int i_nAmount)` surface from the client's method table — but it arrives
as an IIR carrying the *whole* transfer, not as the RPC, which is why `0x202E` never appears on the wire.

## The reply — two ordinary `StatIIR` records

Retail answers with the skill at its new **total**, then the pool at its new total. There is no bespoke reply
message.

```
f98e10b3 0000c350 0802e5d4 01 00000332 0000012d   <- Climbing = 301
f98e10b3 0000c350 0802e5d4 01 000000f3 00000762   <- SkillPoints = 1890
```

The client then **mirrors both records straight back** (c2s `StatIIR`, ~15 ms later). That echo is not a request and
draws no answer.

`RunSpeed` — and only `RunSpeed` — is preceded by the same record with the base byte **clear** (`env` `0x00`).
Sweeping every captured character `StatIIR` shows `env=0` is a property of the **stat**, not of the moment: it is
confined to 22 of the 53 ids seen on a character, `RunSpeed` among them, while `Climbing` / `Hiding` / `Perception`
never carry one. So the extra record is `RunSpeed` being a movement stat with a copy on the n3 dynel layer, not "the
first point in a skill". [V3: SkillPointTests.BuildRunspeedSpendReply_LeadsWithTheDynelLayerCopy]

## The pool

`SkillPoints` (`0x0F3`) rides the FullChar snapshot's stat sheet like any other stat, so it persists in
`CharacterStats` and the spend is written there — the sheet is what the login snapshot is authored from. The
captured necromancer reads **1892** on login and counts down one per spend; that is what is *available*, not a
per-login grant.

## What the server does

`Progression/SkillPointService.cs`, reached from the inbound `SkillIIR` case:

- the request must be addressed to the sender's own handle;
- the transfer must **conserve** points — the pool loses exactly what the skills gain;
- the pool must hold enough, and every credited skill must already have a stat-sheet slot (minting one would put a
  stat on the wire at an ordinal no capture has);
- then the sheet is written and the two `StatIIR` records go out in retail's order.

Refusals are **silent**: retail's client gates the spend itself, so a request that fails here is a client that
already disagrees with us, and there is no captured rejection message to send.

**Sellback is deliberately not implemented.** `DoSkillSellback(int i_eStat)` exists in the client's method table
and the transfer grammar would express a refund perfectly well (pool credit, skill debit), but there is **zero**
sellback traffic in the corpus and the signature carries no amount, so the refund is unrecoverable. Accepting the
reverse direction on trust would let a client cash out the base value every character starts with (`Perception` 560
on the captured necromancer) as 560 free points.

Also unmodelled: a **cap** per skill. The client names `MaxClimbing` / `MaxHiding` / `MaxPerception` /
`MaxRunspeed` (`0x333` / `0x33D` / `0x33F` / `0x43E`), but none of them appears in any captured stat sheet, so
there is nothing to enforce against.
