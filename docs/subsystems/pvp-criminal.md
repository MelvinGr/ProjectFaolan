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
- **Where a criminal revives.** The client's resurrection-point export carries a `shady` attribute
  (`RespawnPoint.Shady`) — 49 outlaw points, one per major playfield, all always-known. `RespawnResolver.Resolve`
  splits the map's points on it: a criminal takes the closest shady one and a law-abiding character never gets
  one, so a release never drops an ordinary player into an outlaw camp. Only 49 of 248 mapped playfields carry a
  shady point, so a criminal who dies anywhere else falls back to the normal selection. This is the death
  destination only — whether retail also uses the shady point as a criminal's bind/recall target is not
  decidable from the RDB, which says WHERE and not WHEN.
- `ShopService` refuses a criminal at vendor trade-open, and re-checks on buy and sell (the open shop is
  remembered per session, so a player who goes criminal with the window already open would otherwise still
  trade). `CriminalStatus.TradesWithCriminals` carries the smuggler exception.
- `.criminal [seconds]` is the GM bootstrap — the retail trigger is killing another player, and PvP
  player-versus-player combat is not modelled, so without it the criminal branches are unreachable.

## Team-mates are exempt, and the exemption is a SPELL RESISTANCE (#869)

Joining a team grants a `Criminal Protection` buff (spell **3880938**), and it never travels alone. In the same
frame, retail sends `DispatchAddSpellResistance(metaType = 2005, percentage = 100)` — and when the buff ends, the
matching `DispatchRemoveSpellResistance` goes with it. Measured across 5 captures: 10 adds and 7 removes, 1:1 with
the buff, every one of them 100%.

`metaType 2005` resolves in **LDB category 55200** — the client's spell-effect / metatype catalog — to **"Criminal
Team"**. So the team-mate exemption is not a special case inside the criminal rules: it is a 100% resistance to a
metatype whose name is literally the thing being resisted, on a registry of its own.

The measured sequence (`login_2-clients_group-trade-friend`):

| t | | |
|---|---|---|
| 53.9 | c2s | `TeamInvite` |
| 56.5 | c2s | `AcceptTeamInvite` |
| 56.9 | s2c | `DispatchYouJoinedTeam` / `DispatchTeamJoin` |
| 56.9 | s2c | **`DispatchAddSpellResistance(2005, 100)`** — once per client |
| 116.8 | s2c | `MagicBuffIIR` `Criminal Protection` **(end)** + **`DispatchRemoveSpellResistance(2005, 100)`** |

⚠️ **The remove repeats the percentage the add carried — it does not send 0.** A removal names the resistance being
taken away, not a new value of zero.

⚠️ **A resistance is a THIRD registry**, beside the stat sheet and the buff list. It is added and removed by its own
message, so a character's resistances are not visible in either of the places the rest of their state lives. The
only other metatype the corpus carries is **427 = "Knockback"**, also at 100% — knockback immunity, which is a
different quantity from the `KnockbackResistance` stat (Gitea #496).

`SpellResistanceCodec` and `SpellMetaTypes` are shipped, with `.resist` as the GM bootstrap. What is missing is a
trigger: nothing grants `Criminal Protection` yet, which is the open half below.

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
