# Factions & reputation (Khitai)

The *Rise of the Godslayer* faction system: twelve Khitai factions in six rival pairs, four player ranks each,
reputation earned by killing the rival's NPCs. This page is the decode and what the server builds from it.

## It is not blocked on captures

The corpus has no Khitai play and no capture of the faction UI or standing packets, but the shipped client carries
everything the system needs:

| what | where | state |
|---|---|---|
| the wire form | `FactionsList` in `DispatchInviteWithInviterFactionPoints` | recovered |
| the faction ids, icons and payout matrix | RDB `1010701`/1 — `data/FactionRelations.xml`, 15.9 KB of plaintext | decoded |
| the display names + the four rank titles per faction | `LDB[254]` | decoded |
| the standing gain/loss message | `LDB[254]` id `27066916` | decoded |
| the point thresholds between ranks | RDB `1000516` — the tuning sheet | decoded |

`--report factions` prints the whole thing and re-derives the joins on every run.

RDB 1010701 is not NPC disposition ([combat.md](combat.md)): it is player reputation, in a different id space.

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

## The payout is per encounter tier

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

## The rank thresholds — in the tuning sheet

What each rank costs is in the place the balance numbers always are: the client's own tweak table
(RDB **1000516**, [client-tweak-table.md](../rdb/client-tweak-table.md)).

| tweak | value | what it fixes |
|---|---:|---|
| `FactionValueRank1` | **50,000** | the rank-1 threshold |
| `FactionValueRank2` | **200,000** | rank 2 |
| `FactionValueRank3` | **450,000** | rank 3 |
| `FactionValueRank4` | **1,000,000** | rank 4 — the top title |
| `MaxFactionPoints` | **1,100,000** | the ceiling standing is clamped to |
| `MinFactionPoints` | **−1,100,000** | the floor, so the scale is signed and symmetric |
| `NeutralMaxFactionPoints` | **5,000** | the top of the neutral band |
| `NeutralMinFactionPoints` | **−5,000** | its bottom |

`dotnet run --project Faolan.Importer -- --report rdb-tweaks --report-arg Faction`.

**The negative half is the part nothing else states.** The ladder is not 0→1,000,000; it is
−1,100,000→+1,100,000 with a ±5,000 **neutral band** in the middle, which is what makes "killing one side's NPC
costs you standing with them and earns it with their rival" expressible: a faction you have farmed against sits
below the band and is hostile.

**The payout matrix and the thresholds agree with each other**, which is the check that they belong to the same
system: at 120 points for a solo normal kill, rank 1 is ~417 kills and rank 4 ~8,333 — and on the group-boss row
(1,800) rank 4 is 556. That is the order of magnitude Khitai reputation is remembered for, and a threshold set
that belonged to something else would have no reason to land there.

The thresholds are absolute point counts, not deltas, and the tweak names carry no per-faction dimension — one
ladder for all sixteen.

## What we implement

The catalog, the ladder and the kill payout are shipped and wired.

| piece | where |
|---|---|
| catalog + payout matrix, imported from the two client sources | `--report`-free module `rdb-factions` → `Faction` / `FactionKillAward` |
| the ladder — bounds, neutral band, rank thresholds | `FactionLadder` (`Faolan.Core/Progression`), every number from RDB 1000516 |
| per-character standing | `CharacterFactionStanding` (signed points; the rank is derived, never stored) |
| the two dispatches | `ApplyFactionPointsCodec` 0x50 / `ApplyFactionRankCodec` 0x4F |
| award on a kill | `FactionService.AwardForKill`, from `CombatService.OnNpcKilled` |
| restore at world entry | `FactionService.SendAll`, after the burst-less entry |

**A kill moves several factions, not one** — the payout list costs the victim's own faction and grants the same
amount to its rival, which is why the award path takes a list rather than a delta. A standing already pinned at a
bound is neither written nor sent, so a grind at the ceiling stops churning the row.

**Both dispatches go out together.** The client keeps the two halves in different places — the bar off the
points, the rank title off the rank — so a server that moved only one leaves the other showing whatever it was
last told. Points first, then rank, since a rank is a threshold *on* the points.

## What is not built

Open work tracked in Gitea #310.

- **The encounter tier.** Tiers 1–5 (solo boss, group normal/boss, raid normal/boss) are imported and only tier 0
  is selectable, because the tier is a property of the encounter — how many players engaged it, and whether the
  creature is a boss — and the simulation models neither. Paying tier 0 for everything **under**-grants a group
  kill rather than over-granting it, which is the safe direction, and the file's own "proportional to 60" rule
  makes tier 0 exactly one player's share of the group rate.
- **The other triggers.** The client also drives standing from scry scripts (`changefactionpoints`,
  `changefactionrank`) and from an `ApplyFactionRankGameAction` element, i.e. quests. That machinery exists and
  has no faction consumer yet.
- **Standing-gated faction vendors** — the shop path with a standing predicate on it.
- **Nothing is verifiable against a capture**: the corpus has no Khitai play, and neither dispatch has
  been observed. The bodies follow the client's own method table and its scalar-argument rule, and the tests assert
  round-trips and the client's own numbers rather than a golden byte string — a self-authored golden here would
  only be testing our encoder against itself.
