# Daily challenges

Retail's daily-challenge system rides **twelve ordinary character stats**. There is no bespoke message, no
challenge-definition table and no separate protocol: the whole thing is counters, pushed through the same
`StatUpdateIIR` (`0x96B8DC59`) everything else uses, and carried on the login snapshot's stat block like any
other stat.

That is why it is implementable from the client plus the corpus, with no capture of the challenge UI at all.

## The stats

The client's stat-name table carries the block contiguously
([aoc-client-stat-names.tsv](../re/aoc-client-stat-names.tsv), ids 1000066–1000077):

| id | name | role |
|---:|---|---|
| 1000066 | `DailyResetTimestamp` | the daily rollover instant |
| 1000067 | `WeeklyResetTimestamp` | the weekly one |
| 1000068 | `MonthlyResetTimestamp` | the monthly one |
| 1000069 | `DailyPvEKills` | category counter |
| 1000070 | `DailyPvPKills` | category counter |
| 1000071 | `DailyMinigameWins` | category counter |
| 1000072 | `DailyQuestsCompleted` | category counter |
| 1000073 | `DailyUnchainedDungeonsCompleted` | category counter |
| 1000074 | `DailyDailyCounter` | challenges completed today |
| 1000075 | `DailyWeeklyCounter` | …this week |
| 1000076 | `DailyMonthlyCounter` | …this month |
| 1000077 | `CounterDailyChallenges` | lifetime tally |

## The caps, and what a cap IS

Every counter has its own tweak in the client's tuning sheet (RDB 1000516,
`--report rdb-tweaks --report-arg Daily`):

```
DailyPvEKillsCap                       5      DailyMetaCap      3
DailyPvPKillsCap                      20      WeeklyMetaCap    15
DailyMinigameWinsCap                   1      MonthlyMetaCap   60
DailyQuestsCompletedCap                3
DailyUnchainedDungeonsCompletedCap     1
```

⚠️ **Read the exe's fallbacks and you get the wrong number every time.** The tweak registry (`FUN_010CB540`)
carries a default for each and **all eight differ** from what the sheet ships (#656). The sheet is the authority.

⭐ **A cap is a THRESHOLD, not a clamp.** The corpus runs `DailyQuestsCompleted` to **7** against a cap of 3, and
`DailyPvEKills` to **13** against a cap of 5. So the counter keeps counting; what the cap decides is the point at
which that category's daily challenge is **complete**. (That answers the open question #656 left: the cap bounds
the challenge, not the counter.)

## The three meta tiers are corroborated three independent ways

`DailyMetaCap` 3 / `WeeklyMetaCap` 15 / `MonthlyMetaCap` 60 are **exactly** the client's own milestone strings in
LDB category 100 — *"Completed 3 Daily Challenges"* (152477172), *"Completed 15 Daily Challenges"* (186060468),
*"Completed 60 Daily Challenges"* (129666260) — and the reward chests escalate to match, as three **consecutive**
item ids with their own descriptions:

| item | name | LDB 50201 description | tier |
|---:|---|---|---|
| 5063646 | Contender's Cache | *"A cache containing basic items to aid you in your travels."* | daily |
| 5063647 | Exemplar's Cache | *"A cache containing valuable rewards to aid you in your travels."* | weekly |
| 5063648 | Paragon's Cache | *"A cache containing rewards of high value!"* | monthly |

The matching feedback strings are *"Daily Challenge Complete"*, *"Contender Challenge Complete"*, *"Exemplar
Challenge Complete"* and *"Paragon Challenge Complete"*; the UI is the **Challenge Journal**, a window with its
own hotkey (`Toggle_ChallengeJournal`). An achievement ladder counts the lifetime tally — *"Complete
10/25/50/100/250/500 daily challenges"*, the last rewarding the title **The Vigilant** (LDB 55700/55701, ids
6867–6874).

So the stat names, the tuning sheet and the LDB agree on the same three-tier ladder without any of them being
derived from the others.

## What the wire shows

⭐ **`DailyQuestsCompleted` ticks when a quest LINE ends — not per quest step.** Five for five across two
captures: a completion immediately superseded by the next step of the same line does **not** tick, while a
completion that ends its line (and raises `MainQuestUpdateIIR`) does.

| capture | t | quest | what followed | tick? |
|---|---:|---:|---|---|
| `xxx_mage-demo_…tortage_city` | 637.6 | 10238 | `QuestFullUpdate` 12369, **same line 241** | no |
| `xxx_mage-demo_…tortage_city` | 819.5 | 12407 | `MainQuestUpdateIIR` | **yes** → 1 |
| `xxx_mage-necromancer_login_play3` | 101.7 | 10287 | `QuestFullUpdate` 10915, **same line 241** | no |
| `xxx_mage-necromancer_login_play3` | 213.5 | 10915 | `MainQuestUpdateIIR`, then a **new line 386** | **yes** → 1 |
| `xxx_mage-necromancer_login_play3` | 329.0 | 10928 | `QuestFullUpdate` 10830, **same line 386** | no |

⭐ **A completion moves four counters together, in one packet.** The three metas and the lifetime tally, never
apart:

```
t=436.4  StatUpdateIIR  DailyDailyCounter 1, DailyWeeklyCounter 1, DailyMonthlyCounter 1, CounterDailyChallenges 1
t=530.0  StatUpdateIIR  CounterPictKills 4, DailyDailyCounter 2, DailyWeeklyCounter 2, DailyMonthlyCounter 2, CounterDailyChallenges 2
```

[V2: xxx_mage-demo_login_play_beach-to-enter-tortage_city.tshark.json t=436.4 / t=530.0 / t=637.6 / t=819.5]
[V2: xxx_mage-necromancer_login_play3.tshark.json t=101.7 / t=213.5 / t=329.0]

The nine counters occur in **46–72** of the 134 captures each, so retail pushes them routinely rather than
exceptionally.

## What we implement

`DailyChallengeService` (`Faolan/GameServer/Progression/`), gated by `DailyChallengeServiceTests`:

* the twelve stats, the five category caps and the three meta caps, as the client states them;
* a quest-line completion steps `DailyQuestsCompleted` — hooked at `QuestProgressionService.CompleteFinal`, which
  is exactly the chain-END completion the table above isolates;
* crossing a category's cap completes that challenge and moves the four counters together, in one
  `StatUpdateIIR` carrying only what changed — the shape the capture shows;
* the daily rollover clears the five category counters and the daily meta; the weekly and monthly metas are
  tallies *of challenges* rather than of activity, so only their own periods clear them, and the lifetime counter
  is never cleared;
* everything persists to the stat sheet, which is what the login snapshot is authored from — so it survives a
  relog, exactly as retail's own snapshots carry these ids.

Minting a sheet row for a character that has never had one goes through `UpsertCharacterStats`, deliberately
separate from `SaveCharacterStats`: that one's refusal to invent an ordinal is a rule the skillpoint spend
depends on, and this is the one case where minting is right.

## Gaps

* ⚠️ **Four of the five categories have no trigger built.** What counts toward each IS now resolved for the two
  kill counters (#887, `--report daily-kills`): `DailyPvPKills` advances once per witnessed NPC death (its PvP
  name is retail's own misnomer) and saturates at its cap of 20; `DailyPvEKills` advances once per FATALITY kill
  and runs past its cap of 5 (13 in one session). Our sim increments neither yet, and `DailyMinigameWins` /
  `DailyUnchainedDungeonsCompleted` have no trigger and no corpus evidence.
* **The completion burst is CAPTURED, at exactly the cap crossing** (#887, 2026-08-31). In four independent
  sessions, the instant a counter reaches its cap the server sends, in one frame: the four challenge counters
  (`DailyDailyCounter`/`DailyWeeklyCounter`/`DailyMonthlyCounter` + `CounterDailyChallenges`), an `UpdateCash`
  with a level-scaled award, and a `DispatchDisplayItemLootChatMessage` granting ONE of item **5063859** (an
  uncatalogued Cache-family id; the message carries the item id twice, a u64 count of 1 and the ACG token
  `LM7Q`). Player-annotated in one session ("daily challenge compelted").
  [V2: xxx_mage-necromancer_login_play1_beach-to-enter-tortage_city.tshark.json t=614.2 / t=758.9]
  [V2: NecroL80_mage-necromancer_login_gameplay_chat-annotated-3.tshark.json t=1048.9]
* ⚠️ **The reward VALUES are only partly pinned.** Cash: +2,629 copper at ~L5 (three completions, two
  characters, both challenge types), +3,893 a level or two later, +100,000 (1 gold) at 80 — level-scaled, not
  per-type; the scale curve between those points is unobserved. Item 5063859's identity (the three tier Caches
  above are 5063646–48) is unresolved, and nothing grants any of it in our sim.
* ⚠️ **The three reset timestamps are never sent in the corpus** — 0 occurrences of 1000066/67/68 across all 134
  captures, where the nine counters appear in 46–72 each. Their unit and epoch are therefore ours (unix seconds
  of the last boundary applied). Nothing observable depends on the choice, since the client is never shown them.
  [V0: the reset timestamps' wire form.]
* The **Challenge Journal** window itself is unmodelled; no capture opens it.

Open work tracked in Gitea #509.
