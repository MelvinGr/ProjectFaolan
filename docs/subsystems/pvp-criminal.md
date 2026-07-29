# The PvP criminal system

Killing another player makes you a **criminal**: a timed debuff during which normal vendors and the wagoneer /
ship-captain travel NPCs refuse to deal with you. A worse tier, **Murderer**, adds city guards turning hostile.

## How it was decoded

The chat-annotated gameplay capture walks the entire mechanic in five annotations, which is why this needed no
guessing:

```
20:15:47  killed player, became criminal 36 murder points
20:19:29  just noticed the criminal debuff i got after killing player (2m remaining)
20:20:20  npc refued to deal with me because of ciminal
20:20:37  now it works (debuf gone)
```

Two independent sources land on the same spell.

**On the wire.** At the kill, stat `0x449` moves — the stat the client's own LDB string table names **"Murder
Points"** — and a `MagicBuffIIR` applies spell `0x3A6657` at the same moment. That buff is removed at 20:20:32,
a **307-second** life, five seconds before the `now it works (debuf gone)` annotation. Five seconds is exactly
the retrospective annotation lag calibrated independently in
[player-actions.md](player-actions.md).

**In the shipped data.** Spell 3827287 is named **"Criminal"** with `DurationMs = 300000` — 300 s, which is that
307 s measured across the buff's re-asserts. Nothing had to be inferred to connect them.

[V2: NecroL80_mage-necromancer_login_gameplay_chat-annotated-4.tshark.json]

## The rules are the client's own words

Both tiers carry their behaviour in their spell **description**, so these are retail's rules verbatim rather
than our model of them:

| spell | name | duration | description |
|---|---|---|---|
| 3827287 | Criminal | 300 s | "Wagoneers & ship captains will not transport you and normal vendors will not trade with you. You can seek out smugglers at outlaw camps instead." |
| 3827285 | Murderer | 24 h | "Murderers are attacked by city guards. Wagoneers & ship captains will not transport you and normal vendors will not trade with you. You must seek out smugglers at outlaw camps instead." |

That sentence is the annotated NPC refusal, and it also ties this subsystem to the travel NPCs of Gitea #506 —
a criminal cannot use them, so the two features meet.

The wider shipped set shows the shape of a ladder: `Criminal` also exists at 20 s (3842479) and twice at 1 h
(3827286, 3827288), alongside `Murderer Protection` / `Criminal Protection` (60 s each) and the
`Increase Murderpoints` (4038603) / `Reduce Murderpoints` (4038602) spells that are presumably the pool's
mechanism.

## What the server models

- `Character.MurderPoints` — the pool, in **wire** units, carried on stat `0x449` in the login snapshot.
- `Character.CriminalUntil` / `Character.IsCriminal` — a persisted expiry, not a bool and not session state:
  in the capture the debuff outlived a zone change into another instance, so it has to survive both.
- `ShopService` refuses a criminal at vendor trade-open, and re-checks on buy and sell (the open shop is
  remembered per session, so a player who goes criminal with the window already open would otherwise still
  trade). `CriminalStatus.TradesWithCriminals` carries the smuggler exception.
- `.criminal [seconds]` is the GM bootstrap — the retail trigger is killing another player, and PvP
  player-versus-player combat is not modelled, so without it the criminal branches are unreachable.

## What is NOT decoded

- **The ladder.** How many murder points promote Criminal to Murderer, and which of the four `Criminal` spells
  a given score selects. The corpus contains exactly one player kill, so there is one point on that curve.
- **Why the pool resets.** Stat `0x449` goes to 3500 at the kill and back to **0** three seconds later. The
  natural reading is that the pool scores a threshold, converts to the debuff and resets — but that is one
  observation, so nothing models it.
- **The refusal message.** Retail evidently tells the player something; the captured refusal was on a travel
  NPC and the packet is not identified, so the server refuses silently (and logs why).
- **Decay.** The pool presumably decays over time. Nothing measured.

Open work tracked in Gitea #503.
