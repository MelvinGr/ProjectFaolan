# The client's own stat-name table

`AgeOfConan.exe` carries a static metadata table that binds **every stat id to a name string**. It is a far wider
name source than LDB category 615: the LDB names none of the `1000000+` range at all, and most of the `0x545..0x593`
run is unnamed there too, while this table names both.

Data: [`aoc-client-stat-names.tsv`](aoc-client-stat-names.tsv) — **1,511** ids,
`statId <tab> hex <tab> clientName`. 1,234 of them are members of `CharacterStatId` under the registry's name; the
rest are ids the enum carries under a name from another source (see "Three name sources" below).

Regenerate with:

```zsh
FROM=01d00000 TO=01e80000 <ghidra>/support/analyzeHeadless <data-repo>/Ghidra aoc \
  -process AgeOfConan.exe -noanalysis -scriptPath tools/ghidra -postScript DumpStatNameTable.java
```

## Shape

Each stat is registered as several 0x20-byte records in a long static-init run, and the **first** record of each stat
is preceded by a `PUSH <name string>`:

```
01de6e16: PUSH 0x1f72a00              ; "MaxEnergy"
01de6e1b: MOV EAX,0x29c6ca8           ; record base + 8 (the variant slot)
01de6e20: MOV dword ptr [0x029c6ca0],0xf425d   ; the stat id
01de6e2a: MOV dword ptr [0x029c6ca4],0x0       ; the record's kind
01de6e34: CALL 0x010daa10             ; store a STRING variant (type tag 9)
```

The stat's remaining records take the same id with a different kind and store an *int* variant (type tag 4) through
`FUN_010da850` / `FUN_010da880`, and carry no `PUSH` — hence one name per stat. None of them holds a game value: the
ints are small kind/flag words, so this table is metadata, not defaults.

## Why the names can be trusted

The table is self-checking against ids recovered from entirely unrelated directions:

- `804` `Strength` and the `805..815` `Max*`/attribute run — the ids in `CharacterStatId` from the character sheet.
- `1071` `OffhandChance` — measured in-client as Offhand Attack Chance.
- `1351` `NaturalEnergyRegeneration` — LDB category 615 names the same id "Natural Energy Regeneration".

## What it named that nothing else could

**The whole spawn scaffold.** All 31 of the `0x545..0x593` word stat ids in `SpawnPreIir.WordStatIds` are in the
table, and they are exactly the PvP / minigame career that scaffold's decode predicted — `PvPFatalities` (`0x564`),
`PvPMachesAbandoned` (`0x560`), the `Annihilation`/`CTF`/`Conquest`/`Siege`/`Tournament` won-lost pairs, and the
`Onslaught*` wave counters. Two independent corroborations fall out:

- the scaffold's two float slots are `0x545` **`ResurrectionTime`** and `0x546` **`ResurrectionTimePvP`** — timers,
  which is what a float belongs to;
- `0x1E86AE` (word 18) is **`SelectedTag`**, and the flag bytes `0xF4261`/`0xF4260` are **`NumFeatSpecs`** and
  **`ActiveFeatSpec`** — matching the feat-spec sublist that sits in the same scaffold.

**The energy pool** — `1000028` `Energy`, `1000029` `MaxEnergy`, `1000031` `EnergySprintDrain` — which is the model
behind the yellow sprint/spellweave bar. See [`../subsystems/combos.md`](../subsystems/combos.md).

**`1000070` `DailyPvPKills`** and `1000069` `DailyPvEKills`, the counters paired with `ChainKillIIR_t`.

## Key on the descriptor slot, not the stored value

Pairing a name with any store whose value *looks* like a plausible stat id is a guess about the data rather than a
bound on the table, and below 768 the false pairs dominate (a nearby audio-preset init stores `8` "Cave",
`11` "Carpeted Hallway", `2` "LoadingMusic" in exactly this shape).

**Key on the descriptor slot** — the address the id is written to. The descriptors live in two contiguous global
regions and the id sits at a fixed offset inside each 0xA0-byte record:

| region | what it holds | id at `slot % 0xA0` |
| --- | --- | --- |
| `0x0298F000..0x029CC000` | the main table | `0x00`, `0x20`, `0x40`, `0x60` (1,361 of them) or `0x80` |
| `0x021B8000..0x021BC000` | the `1000000+` table | `0x98` |

That is a statement about the table, so it does not care what the value looks like, and it comes with its own
check: a descriptor writes its id exactly once, so a correct filter yields **no duplicate ids**. It yields 1,511
rows and 1,511 distinct ids. What the offset set excludes is the point — `0x44` and `0x64` are two neighbouring
fields a descriptor also writes, and they are where the head-of-region artifacts live (four descriptors whose kind
word is 8, which would otherwise claim id 8 from `TimeExist`).

**Consume and emit are different tests.** The pending name is consumed by the first store into the table carrying
a non-zero value — not by every store (which pairs a name with a kind word, losing `GameAction_LocalizerType` and
seven others) and not only by an id-slot store (which lets a name run past its own descriptor into the next one's
id, mislabelling `3 AttackSpeed` as `VolumeMass`).

The slot-keyed sweep is a strict widening of a value-filtered one: all 747 names the value-filtered sweep finds are
reproduced with **zero disagreements**, and 764 ids are added, 762 of them below 768.

The retained rows are not individually verified beyond the spot checks above — treat a name as strong evidence, not
as a citation, until the id is used.

### What the sub-768 half named

- **`0x069` `NextPowersessionReset` / `0x06A` `PowerSessionRemaining`.** `PowerSessionRemaining` is in the
  captured snapshot at `0x14` — a full session of 20. Stat 106 counting 20/19/18 down while `DailyPvPKills`
  counts 0/1/2 up is a **power session being spent by kills**, not a second kill counter.

  **The game's own documentation names the player-facing concept.** The official quickstart guide calls them
  **Fresh Kills**: *"You get 20 Fresh Kills every day. The number displayed at the end of the bar are 'Fresh
  Kills', which grant extra XP when killing worthy monsters."*
  ([ageofconan.com/quickstart](https://www.ageofconan.com/quickstart)) So `PowerSessionRemaining` is the number the
  client draws at the end of the XP bar, `NextPowersessionReset` is when it refills (daily), and `0x0C3`
  `PowerSessionBonusesCounter` is the third member of the trio.

  **The corpus's own dynamics corroborate the wording, including the word *worthy*.** `--report stat-series
  --report-arg 106,1000069,1000070` over the whole `StatIIR` stream:

  | stat | records | runs | range | moves |
  |---|---:|---:|---|---|
  | `106 PowerSessionRemaining` | 65 | 10 | 20..11 | 23, **all down** |
  | `1000069 DailyPvEKills` | 278 | 53 | 0..13 | 39, **all up** |
  | `1000070 DailyPvPKills` | 342 | 55 | 0..20 | 120, 118 up |

  Three separate create captures show `106` at **20** on a brand-new character, and it never once increases —
  a depleting pool, exactly as *"20 Fresh Kills every day"* predicts. And it is **not** spent one-for-one:
  in `xxx_mage-necromancer_login_play1` the same session moves `106` six times while `1000070` moves twenty —
  most kills cost no Fresh Kill.

  **Every captured character's login snapshot carries `0x06A`**, at `20` for Hakotep / DemoL80 / AssassinL23 and
  at **`17` for NecroL80**, the character a live observation caught three kills into her session. Nothing else in
  the trio is in the snapshot, so the reset instant and the bonus counter arrive (if at all) as stat updates.

  Unknown: **how much extra XP a Fresh Kill grants, and what makes a monster "worthy"**. Neither the client's
  names nor the guide says, and no capture measures it.

### The daily counters are a whole subsystem

`DailyPvEKills` / `DailyPvPKills` are two members of a **daily-challenge** family the registry names in one run,
and the client registers a **cap tweak for each of them**:

| stat | name | its cap tweak | exe default | shipped in RDB 1000516 |
|---:|---|---|---:|---:|
| `1000069` | `DailyPvEKills` | `DailyPvEKillsCap` | 30 | **5** |
| `1000070` | `DailyPvPKills` | `DailyPvPKillsCap` | 30 | **20** |
| `1000071` | `DailyMinigameWins` | `DailyMinigameWinsCap` | 3 | **1** |
| `1000072` | `DailyQuestsCompleted` | `DailyQuestsCompletedCap` | 3 | **3** |
| `1000073` | `DailyUnchainedDungeonsCompleted` | `DailyUnchainedDungeonsCompletedCap` | 1 | **1** |

Above them sit the meta counters and their own caps, and three reset instants:

| stat | name | | tweak | exe default | shipped |
|---:|---|---|---|---:|---:|
| `1000074` | `DailyDailyCounter` | | `DailyMetaCap` | 5 | **3** |
| `1000075` | `DailyWeeklyCounter` | | `WeeklyMetaCap` | 10 | **15** |
| `1000076` | `DailyMonthlyCounter` | | `MonthlyMetaCap` | 15 | **60** |
| `1000077` | `CounterDailyChallenges` | | | | |
| `1000066/67/68` | `Daily`/`Weekly`/`MonthlyResetTimestamp` | | | | |

(`75 MetaType` is the family's other member.) The exe defaults come from the client's tweak registry
(`FUN_010CB540`, which registers every tweak with a fallback `double`); the shipped column is
`--report rdb-tweaks --report-arg Daily`, i.e. what the tuning sheet actually ships. **Every one differs from its
default**, so the binary's fallbacks are not the game's numbers.

What the two kill counters count (`--report daily-kills`):

- **`1000070` advances once per NPC death the client is told about**: where a capture's deaths stay under 20 its
  increments equal the death count exactly (9=9, 10=10, 11=11, … on 7 of 15), and four captures running
  29 / 34 / 37 / 39 deaths all stop dead on 20 — its own `DailyPvPKillsCap`, in three independent captures
  (`xxx_mage-demo…beach-to-enter-tortage`, `xxx_mage-necromancer…play1`, `Hoxxling…gameplay`). So the server clamps
  it — under a PvP name, in a corpus with no PvP.
- **`1000069` is the daily fatality count**: the report pairs 49 of 49 of its bumps with the Fatality self-buffs
  landing on the player (and 49 of 49 applies with a bump), the lifetime `1379 PvEFatalities` riding the same
  burst. It passes its cap of 5 without stalling, reaching 13: that cap is the daily fatality challenge's
  completion target, not a clamp — crossing 5 fires the challenge-completion burst (see
  [daily-challenges.md](../subsystems/daily-challenges.md)). Nothing in the image reads `DailyPvEKillsCap` or
  `DailyPvPKillsCap`; their only reference is `FUN_010CB540`, the defaults registry that declares them.
- **They are not one stream under two names**, and the id↔name pairing is the client's own: the stat registration
  (`FUN_01df7df0`) writes `0xF4285` before naming `DailyPvEKills` and `0xF4286` before `DailyPvPKills`, and in a
  single run (capture `…play1`, handle `0x01317CF8`) `1000069` advances **13** times while `1000070` advances
  **20**. A swapped pairing would make `1000070` a counter with a cap of 5 overrunning it fourfold.

The neighbouring config keys say what the subsystem is embedded in: `PvEXPFromPvPMultiplier`,
`MinigamePvEXPMultiplier`, `PvPXPFromPvPCapMultiplier`, `PvEXPFromPvPGroupPenalty`, `TWK_MinLevelForPvPXP`,
`ServerLevelCap` and `CurrentSeason`.

### The two `Deaths` ids

Both names are the registry's and they name **different counters**:

| id | client name | what it is |
|---|---|---|
| `0x292` | `PvPTotalDeaths` | the PvP **career** total — 0x290 `PvPTotalKills`, 0x291 `PvPFinishingBlows`, 0x293 `PvPMatchesWon`, 0x294 `PvPMatchesLost` either side of it |
| `0x542` | `Deaths` | a per-match **minigame scoreboard** column — 0x53C `DamageDone`, 0x53D `HealingDone`, 0x53E `CrowdControlDone`, 0x53F `DamageAbsorbed`, 0x540 `EnemyFlagsTaken`, 0x541 `KillingBlows`, this, 0x543 `TimeInDominationZone` |

Three independent statements put `0x292` in the PvP career run, and the third is the strongest because it is not
a name at all:

1. the registry says so;
2. **contiguity** — `0x291`, `0x293` and `0x294` are ids the enum agrees with the registry on, so a bare `Deaths`
   at `0x292` would be a hole punched in an otherwise-matching run;
3. **retail's own captured `SubClassStatBlock`** pairs each of the two with its `*Seasonal` twin — `0x566`
   `PvPTotalKillsSeasonal` and `0x567` `PvPTotalDeathsSeasonal` — four slots apart. A seasonal twin's partner is
   the career total of the same thing, and that is wire evidence rather than a naming judgment.

A PvE kill or death therefore moves the client's separate `NPCKills` (`0x298`) / `NPCDeaths` (`0x299`), not these
two; our server keeps `Character.NpcKills` / `NpcDeaths` for that. No capture carries a live update of any of the
four ids, so the corpus cannot show which one retail moves; a live retail `StatHolder` dump (the `NecroL80`
character's) is the oracle.

The engine's Anarchy-Online lineage shows through the low ids — `4 Breed`, `13 MagicBurden`, `17
MemorizationSlot` — which is itself a check on the range: those are names nothing in this codebase would invent.

## Three name sources, and which to cite

The client carries **three** name sources for a stat id, and they are not interchangeable:

| source | what it is |
| --- | --- |
| **LDB category 615** | the **display** name — localized, shipped in seven languages, and what the client's own tooltip prints |
| **LDB category 633** | an **internal** identifier list, English only |
| **this table** (the exe's static descriptors) | also internal, and a mix — it sides with 633 sometimes and with 615 others |

The registry and LDB 615 disagree on **306 ids** (`--report ldb-stat-names` prints every one). Over the 341 ids that
carry both a 615 and a 633 name, **263 agree (77%) and 78 disagree**; most disagreements are cosmetic
(`Default Position` / `DefaultPos`, `Bonus XP` / `XPBonus`), but some are a different claim entirely. Neither
source wins wholesale; each has a failure mode:

**LDB 615 was not fully re-authored when slots were repurposed.** Where it names an Anarchy Online mechanic, the
registry is right:

| id | LDB 615 says | the exe registry says |
|---:|---|---|
| `0x009` | Map Flags | `MaxConsciousness` |
| `0x016` | AMS | `SpellCategory` |
| `0x02E` | Bandolier Slots | `ItemSource` |
| `0x030` | Organization rank | `ClanLevel` |
| `0x0A9` | ChosenGod | **`HeroicMode`** |

`Organization` is Anarchy Online's word for a guild; `AMS` and `Bandolier Slots` are AO mechanics that do not exist
in this game.

**The internal lists can be flatly wrong about a field's identity**, and there LDB 615 is right:

| id | LDB 615 (display) | 633 / this table (internal) |
| ---: | --- | --- |
| `1037` | **Knockback Resistance** | `Weight` |
| `147` | **Spell Critical Chance** | `MaxSneak` |
| `120` | **Money** | `PickPocketCash` |
| `8` | **Duration** | `TimeExist` |
| `294` | **Casting Time (Seconds)** | `ItemDelay` |
| `129` | **Spell Critical Damage** | `SpellCriticalModifier` |

`1037` is the clearest case: `CharacterStatId.KnockbackResistance` is live code — `KnockbackService.Knock` reads it
and `BuffStatEffects` honours it — and 615 names it *Knockback Resistance* in all seven languages while this table
says `Weight`. The internal list also still carries AO vocabulary in places (`NanoFocusLevel` for `355`, where this
table says `LastPvPXP`).

**The rule:** cite LDB 615 for what a user-facing stat means, unless its name is an AO leftover; use this table for
coverage — it names the `1000000+` range and the `0x545..0x593` run that 615 does not reach at all, and for an id
615 never names it is the only AoC-era evidence there is. Our own `CharacterStatId` inherited AO names from an
Anarchy Online stat list (`AwardedOmniTekTokens` — OmniTek is an AO faction — and `NanoInterval`); 120 members name
a different thing from the registry, and the report splits them from the 38 that are the same concept reworded.

## A name is not a consumer: `HeroicMode` (`0x0A9`) is stored and never read

The exe's descriptor registry (`FUN_01db2740`) pairs `PUSH "HeroicMode"` with `MOV [0x029959c0], 0xa9` on the very
next instruction; LDB category 615 says `ChosenGod`. The exe's name wins here: `player_stats.heroic_mode` is a real
field of the client's own protobuf schema while nothing named for a deity is, and AoC has no player-choosable
religion. (The one captured character carrying `1` is a necromancer.)

Either way **the client cannot say what a value means, because it never looks at the value.**

`ConstScan CONST=a9` finds exactly **two** uses of `0xA9` as a stat id in the whole image, and both are writes —
`FUN_007a2ad0` (`SetStat(0xa9, *(obj + 0x14c))`) and the stat-apply run in `FUN_01458d40`, where it is one of a
block (`0xd7 GmLevel`, `0xed`, `0x42`, `0xa9`) copied out of consecutive fields of a server-fed struct. Every
other `PUSH 0xa9` in the binary is an assert's line number (`formationdata.cpp`, `DepthPrepassShader.cpp`,
`fmod_codec_wav.cpp`, …). There is **no** `GetStat(0xa9)` anywhere.

It is not that the stat is ignored, either — one class overrides its stat accessors to give this single id a
dedicated slot:

```c
// FUN_00843d10 / FUN_00843d40, adjacent in vtable 0x01fd2640
if (param_1 != 0xa9) { return FUN_0080ab20(param_1, param_2); }   // everything else: the generic stat bag
return *(undefined4 *)(in_ECX + 0x244);                            // 0xA9 lives here
```

…and those two functions are the only ones on that class that touch `+0x244`. So the client caches the value,
mirrors it, and consumes it nowhere.

**That makes `0x0A9` a server-side number**, the same tell as the `Combat_{PvE,PvP}*ShieldBonus` tweaks the
client registers and never reads ([`../rdb/client-tweak-table.md`](../rdb/client-tweak-table.md)). There is no
value mapping hidden in the client to recover, and no in-client check that could confirm one, because nothing the
client draws depends on it.

**The general rule:** the stat-name table tells you what an id is called, not that the client does anything with
it. Before treating a named id as decodable from the binary, check that something reads it.

## The table does not start at 0 — ids 0, 1 and 2 are absent, and stat 1 is real

The dump's lowest entry is **3 `AttackSpeed`**. That is a property of the table, not of the id space, and reading
"not in the exe registry" as "not a stat" gets exactly three ids wrong — one of which is common.

**Stat `1` is `HealthMax`** (LDB 615 calls it `Health`, and our `CharacterStatId.HealthMax = 0x0001`). It is used
across every subsystem that carries stat modifiers:

| table | rows with `StatId = 1` | of |
|---|---:|---:|
| `FeatEffects` | 1,397 | 23,033 |
| `SpellStatEffects` | 505 | 25,716 |
| `ItemStatModifiers` | 245 | 6,167 |

and the feats carrying it are named for what it is — `HP 40-60 4`, `HP 60-80 5`, fourteen rows each. The 1,397
`FeatEffects` rows sit on exactly two `(statId, kind)` pairs — both `statId = 1`, on `Modify` (53045) and
`InterpolatedModify` (53220): a real stat below the dump's floor, not noise.

So when using this file as a name oracle, treat a miss on an id **below 3** as "outside the dump" rather than as
"unnamed", and fall back to `CharacterStatId` / LDB 615 for those three.
