# Alternate Advancement: the xp pools, the give-away and the perk purse

What our server implements of AoC's alternate-advancement system. The decode side — where the client keeps the
trees, how a perk purchase reaches a server, and the whole `FeatWindows.AAFeatTree` API — is
[`../re/alternate-advancement-perks.md`](../re/alternate-advancement-perks.md); this page is the rules.

**AA is not post-80 content.** All twelve trees gate at levels **20–80**, so a perk ladder runs alongside
ordinary levelling from 20 up rather than starting where levelling stops.

## AA adds no messages

A perk is a **feat**: the client's `TrainAAFeat` goes through the same `FeatHolder` train virtual a class feat
does, so an AA purchase arrives as an ordinary `FeatIIR` state-`0x04` train request carrying a perk id. There is
not one AA method in the client's 860-method RPC catalog. Everything below therefore rides three channels that
already existed:

| what | message | who sends it |
|---|---|---|
| the grant announcement | `FormatFeedbackIIR` (`0x8D5A8B6B`), token `AlternateAdvancementPvEXpGranted` | `AlternateAdvancementXpCodec` |
| the pool itself | `StatIIR` (`0xF98E10B3`) on `AAPvEXP` (1000007) / `AAPvPXP` (1000006) | `StatCodec` |
| the token wallet | `DispatchUpdateTokenAmount` (`0x204D`) | `UpdateTokenAmountCodec` |

## What a kill pays

**Retail announces every grant**, and the announcement states the amount verbatim in its first `<parameter>`.
The `AAPvEXP` push in the same frame moves by exactly that much on all fourteen kill-grants in the corpus, so the
accrual rate is transcribed off the wire rather than fitted. `--report aa-xp` re-derives the whole table.

The ordinary payers — `NpcStatCore.NpcType` 4 with no flag word — against a level-80 killer:

| creature level | gap | AA xp | seen as |
|---:|---:|---:|---|
| ≤ 72 | ≤ −8 | **0** | 30+ kills over eight sessions, level 71 and 72 among them |
| 73 | −7 | 27 | Zingaran Operative, Hired Bodyguard |
| 74 | −6 | 57 | Hired Bodyguard |
| 75 | −5 | 91 | Zingaran Operative, Hired Bodyguard |
| 76 | −4 | *131* | unobserved — interpolated |
| 77 | −3 | *171* | unobserved — interpolated |
| 78 | −2 | 211 | Wharf Rat Thug (twice) |
| 79 | −1 | 234 | Wharf Rat Thug |
| 80 | 0 | 258 | Ravenous Crocodile Follower |

Repeats are byte-identical (27/27, 91/91, 211/211), so the amount is deterministic per level — there is no roll
to model. The two italic rows are the only invented numbers in the system and are marked as such at their line in
`AlternateAdvancementRules`; a capture of a level-76 or level-77 kill replaces them.

Three rules ride on top of the table:

* **The accrual gate is the killer's level, not just the gap.** Every kill-grant in the corpus was made by a
  character at the level cap, and no kill in any levelling capture pays anything — while a flat item redemption
  pays a Tortage lowbie 15,000. Kills feed the pool only at the cap; below it the pool moves by rewards alone.
* **The killing blow attributes the grant.** A level-75 kill whose last hit came from the necromancer's pet
  paid nothing although the player had damaged it moments before, and a level-78 kill where the pets dealt the
  clear damage majority but the player landed the last hit paid in full. Majority-damage is refuted.
* **The multiplier has a column**, read off the creature's template rather than off its reputation: `NpcType` 2 is
  the named tier and pays about double, and `NpcType` 4 with `NpcFlags` `0x1C0000` pays about 15% more. A boss
  below the cutoff pays a floor of **2** where an ordinary grey pays nothing.

**The two factors are approximations, and the corpus says so itself.** Argus the Thief (level 79, type 2) paid
461 against the ordinary 234 and Eerika of the Maidens (level 80, type 2) paid 510 against 258 — ratios of 1.970
and 1.977, not 2. 461 is odd, which rules out an integer ×2 over an integer base outright, so the real
mechanism scales something other than the ordinary amount. One sample per case cannot recover it; 2.0 reproduces
both within 1.5%. The flag does not compound on a type-2 — Eerika carries it and still pays the named rate.

## The give-away

The pool's denominator is the client's own `AlternateAdvancement_XPCap` = **1,000,000**, and the client does the
division itself: `GetAANextGiveAwayCompletionPercentage` is literally `100 * GetStat(pool) / XPCap`. So the server
sends the running total and nothing else.

Reaching the cap pays **3 tokens into the pool's own specific wallet plus 1 generic**
(`AlternateAdvancement_{Specific,Generic}TokensAwarded`), and the remainder carries over. Those two tweaks have
zero client reads against the cap's six, which is what says the pay-out half of the economy is the server's.
The **100 unspent tokens per pool** ceiling likewise: its three `*UnusedTokensCap` key names are absent from the
exe. It is applied to the credit, not to the stored total — the wire carries lifetime `gained`/`spent` and the
client subtracts, so clamping the total would make an already-spent token reappear.

## The purse

`CharacterToken` is one wallet per `(character, pool)`, holding the two lifetime totals the wire carries. A row
per pool the character has touched rather than 29 per character: retail states one pool per message and only when
it moves — ten pushes across the whole corpus, each a single pool — so an absent row and a 0/0 row mean the same
thing to the client.

**There is deliberately no wallet burst at world entry.** Retail sends none, and the pool stat's first push in
every session lands beside a grant, hundreds to thousands of packets in.

## Buying a perk

A train request is split into perks and ordinary feats before either budget is consulted, because they are two
currencies:

* an ordinary feat costs **one point per rank** against the level budget (`FeatRules`);
* a perk costs its **`FeatPerkCost` row** out of an AA token pool — 3,096 rows off RDB 1000099, priced per
  placement, so the same perk in two classes' trees is two rows and the price is looked up per class.

Charging a perk to the feat-point budget would overspend the wrong purse and leave the tokens untouched. The perks
stay inside the prerequisite graph, though — an AA tree has tiers like any other —
so the tree edges are checked against the whole resulting set.

A mixed request is all-or-nothing: affordability is checked for every pool before anything is spent, so a player
who can afford two of three perks does not end up having paid for two and trained none. Only new perks are
charged; the client re-sends the resulting set often enough that charging per request would bill twice for one
perk.

216 placements are priced in both specific pools at the same price. The PvE one is taken, because that is
the pool a PvE server can actually fill; nothing in the client picks for us.

## Driving it

The retail trigger is out of reach on a dev character — a kill pays only at the level cap, and no level-1 character
can fill a 1,000,000-xp pool. `.aa` is the bootstrap:

```
.aa                     report the pools and every wallet
.aa xp <amount>         pay AA xp into the PvE pool (the give-away fires if it fills)
.aa token <pool> <n>    credit one pool outright, e.g. `.aa token AltAdvancementPvEToken 40`
```

Everything downstream of the event — the announcement, the pool push, the give-away, the wallet update — is the
ordinary path.

Open work tracked in Gitea #150.
