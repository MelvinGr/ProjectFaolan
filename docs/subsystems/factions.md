# Factions & reputation (Khitai)

The *Rise of the Godslayer* faction system: twelve Khitai factions in six rival pairs, four player ranks each,
reputation earned by killing the rival's NPCs. **None of it is implemented** — this page is the decode, so the
implementation has data to build against. Open work is tracked in Gitea #310.

## It is not blocked on captures, and never was

#310 filed the system as *"blocked on expansion (Khitai) content + captures of the faction UI/standing packets to
decode the wire form — none today."* Both halves are false against the shipped client:

| what | where | state |
|---|---|---|
| the wire form | `FactionsList` in `DispatchInviteWithInviterFactionPoints` | recovered |
| the faction ids, icons and payout matrix | RDB `1010701`/1 — `data/FactionRelations.xml`, 15.9 KB of plaintext | decoded |
| the display names + the four rank titles per faction | `LDB[254]` | decoded |
| the standing gain/loss message | `LDB[254]` id `27066916` | decoded |
| **the point thresholds between ranks** | — | **not in the client** |

`--report factions` prints the whole thing and re-derives the joins on every run.

⚠️ RDB 1010701 had been opened once before and set aside — [combat.md](combat.md) records it as *"chased for [NPC
disposition] and is player REPUTATION in a different id space."* That was the right verdict for **that** question
and exactly the wrong thing to carry forward: a table that is not what one ticket wanted is still the table
another ticket is blocked on.

## The catalog (RDB 1010701/1)

16 factions carry a standard rate. The twelve Khitai ones sit in six rival pairs; killing one side's NPC costs you
standing with them and earns the same amount with their rival.

| id | faction | rival | icon |
|---:|---|---:|---:|
| 10 | Children of Yag-Kosha | 20 | 4576799 |
| 20 | Yellow Priests of Yun | 10 | 4576798 |
| 30 | Shadows of Jade | 40 | 4576794 |
| 40 | The Brittle Blade | 30 | 4576788 |
| 50 | The Last Legion | 60 | 4576792 |
| 60 | The Scarlet Circle | 50 | 4576793 |
| 70 | Tamarin's Tigers | 80 | 4576795 |
| 80 | Scholars of Cheng-Ho | 70 | 4576796 |
| 90 | Wolves of the Steppes | 100 | 4576797 |
| 100 | The Hyrkanians | 90 | 4576789 |
| 110 | The Insurrection | — | 4576790 |
| 120 | Jiang Shi | — | 4576791 |
| 200 | Ela Shan / Hand of Glory | — | 4839657 |
| 310 | Lord Stoll / Pit Masters PvE | — | 5037455 |
| 320 | Lord Stoll / Pit Masters PvP | — | 5037454 |
| 330 | Clan Vigdis | — | 5038906 |

## The payout is per ENCOUNTER TIER

A relation id is `factionBase + tier`, so `id=13` is faction 10 at tier 3. Only the twelve Khitai factions carry
the override set; the other four have the standard rate only.

| tier | encounter | points |
|---:|---|---:|
| 0 | solo normal | 120 |
| 1 | solo boss | 180 |
| 2 | group normal | 720 |
| 3 | group boss | 1800 |
| 4 | raid normal | 1440 |
| 5 | raid boss | 2400 |

The file states its own design rule in a header comment: *"Faction points are usually proportional to 60. This way
if people make a team and start grinding reputation, there is no situation where an NPC is worth more in team than
alone."* So a 6-person group killing a 720-point mob earns 120 each, exactly what a solo kill of a 120-point mob
gives — and raid normal is 1440 because a 24-player raid clears in groups of six.

## Ranks (LDB 254)

Nine of the sixteen carry a player rank ladder: ids **10 through 90**. The seven without one are 100 (the
Hyrkanians — the only faction that has a rival yet no ladder, so the Wolves' rivalry is one-sided from the
player's side), 110 and 120 (Insurrection, Jiang Shi — no rival either), and the four non-Khitai ids 200/310/320/330,
which carry no encounter-tier overrides either. All seven look like NPC-only reputations a player never joins.

| id | faction | rank 1 | rank 2 | rank 3 | rank 4 |
|---:|---|---|---|---|---|
| 90 | Wolves of the Steppes | Brigand | Raider | Bandit | Wolf of the Steppes |
| 80 | Scholars of Cheng-ho | Initiate | Reader | Savant | Scholar |
| 70 | Tamarin's Tigers | Venturer | Freebooter | Mercenary | Tiger |
| 60 | Scarlet Circle | Neophyte | Thaumaturge | Sorcerer | Summoner |
| 50 | Last Legion | Soldier | Captain | Commander | General |
| 40 | Brittle Blade | Thug | Strangler | Stalker | Blade |
| 30 | Shadows of Jade | Pickpocket | Footpad | Thief | Jade Shadow |
| 20 | Yellow Priests of Yun | Acolyte | Monk | Priest | Archpriest |
| 10 | Children of Yag-kosha | Pilgrim | Believer | Mystic | Sage |

The LDB blocks are **ordered runs, not keyed by faction id** — names at `61205296 + 16n` (bare) and
`60435760 + 16n` (with the article), rank titles at `59456385 + 0x1000·n + rank`. The report joins them to the
catalog **by name** rather than by position, and prints the resulting id order: `90,80,70,60,50,40,30,20,10`, i.e.
the LDB lists them in the reverse of the XML's order. Each faction's rank-4 title is an independent second check
that the row is the right one — `Wolf of the Steppes`, `Tiger`, `Jade Shadow`, `Archpriest`, `Sage` each name
their own faction, so an off-by-one or a flipped direction shows up immediately.

## The wire

`FactionsList` is an ordinary `ListFrame` over pairs:

```
FactionsList = [u32 (count + 1) * 0x3F1]  then count x { [u32 factionId][u32 points] }
```

Recovered from `DispatchInviteWithInviterFactionPoints` (CSPlayerInterface `0x0B`), whose three fields are named
by its own bail-out strings (`inviter`, `message`, `factionPoints`); the reader divides by `0x3F1` and rejects a
remainder, and the caller builds a red-black-tree sentinel, so it is a `std::map<u32,u32>` behind the list frame.
Zero occurrences in the capture corpus.

## What is still missing

**The point thresholds between ranks.** The client ships the four rank names and the per-kill payouts and nothing
that says what Rank 2 costs — so a ladder would have to be invented, and an invented threshold is
indistinguishable from a decoded one the moment it is written down. Left open.

Also still absent, and squarely implementation rather than decode: the `Faction` / `FactionStanding` model, the
kill hooks that award points, and the standing-gated faction vendors.
