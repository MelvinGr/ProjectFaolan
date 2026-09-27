# The client's balance-tweak table (RDB type 1000516)

RDB type **1000516** is a single entry holding the game's own tuning sheet: **294 named `double` constants**
("tweakables"). It is the authority for numbers the server otherwise has to guess — regen rates, armour targets,
combat chances — and for a set of spell/buff ids the server is expected to cast itself.

Dump it with:

```zsh
dotnet run --project Faolan.Importer -- --report rdb-tweaks --Content:RdbDir <rdb-dir>
dotnet run --project Faolan.Importer -- --report rdb-tweaks --report-arg regen --Content:RdbDir <rdb-dir>   # name filter
```

## Grammar

```
[u32 type=1000516][u32 0][u32 1][u32 count]
count × ( [u32 nameLen][nameLen × ASCII][f64 value, little-endian] )
```

No alignment padding and no terminator; the declared `count` is exact (294 names parse to exactly the blob
length). The payload is zlib-compressed like every other RDB entry, so read it through `ReadDecompressed` — it is
NOT one of the TEA-encrypted types.

## Why it is trustworthy

Two constants independently confirm the parse, because we decoded them from elsewhere first:

- `TWK_NanoExhaustedEffect` = **2960** — the exact spell id decoded as the energy-depletion "Exhausted" debuff
  the client applies at energy 0 (`docs/subsystems/combat.md`).
- `TWK_Selection_Radius` = **10** — the interaction/selection range.

A misaligned or wrongly-typed read could not land on either.

## Over HALF the sheet is the SERVER's, and the decisive test is the exe's string table

A tweak the client uses is **registered by name**: a 5-instruction thunk that pushes the name string, binds it to
a global slot and installs an initialiser (see `tools/ghidra/TweakGlobals.java`). So a key whose NAME does not
occur in `AgeOfConan.exe` at all is never registered, never bound to a client global, and cannot be named by any
client code. That is a stronger test than counting references — it needs no Ghidra and cannot produce the
slot-vs-slot+8 false negative described below:

```zsh
dotnet run --project Faolan.Importer -- --report rdb-tweaks | grep '^  ' | awk '{print $1}' | sort -u > /tmp/keys
strings -a -n 4 "<client-dir>/AgeOfConan.exe" | sort -u > /tmp/exe
comm -23 /tmp/keys /tmp/exe        # the keys no client code can name
```

**158 of the 294 keys — 54% — are absent.** RDB 1000516 is not "the client's tuning sheet with some server
numbers in it"; it is the **game's** tuning sheet, shipped inside the client's data, and most of it is knobs the
client cannot even name. That settles the question for the whole table at once (the same argument covers
`Combat_{PvE,PvP}{0..3}ShieldBonus` and the recast constants): **when a key is in the absent set, its value is ours to apply.**

What the absent set is, by cluster:

| cluster | n | what it is |
|---|---:|---|
| `WorldBoss_*` | 28 | the world-boss spawn CALENDAR (`SpawnDay_Boss_1..12`, `SpawnWeek_Boss_1..12`, `SpawnTimer` 1800, `SpawnTimerRandom` 1800, `RareLootChance` 25) |
| `PvP_MonthlyFestival_*` | 39 | the festival calendar plus its three game modes (Arena / Juggernaught / KingOfTheHill durations, warmups and kill XP) |
| `Camera_Path_Normal_*` | 24 | a four-point position/tangent camera spline |
| `Onslaught_*` | 8 | per-wave HP/damage scalars (1.0185 normal, 1.025 / 1.04 boss) and the bar target (30 + 2 per level) |
| `Quest_*` | 4 | `MaxGetDistance` 5, `LeaveBoundedAreaTimeout` 10, `TimeToCompleteMultiplier` 60, `CooldownTimeoutMultiplier` 60 |
| `AIDefaults_*` | 3 | `MaximumChaseRadius` 45, `ChaseDeaggroDelayTime` 5, `IrritationForgetPeriod` 4 |
| `TWK_Consequence_*` | 3 | `DailyDecayRate` 12, `QuestDecayRate` 12, `XPGainDecayRate` 199 |
| `Renown*` | 2 | `RenownCacheLimit` 1000, `RenownSaveInterval` 300 — plainly a server cache and its flush period |
| the rest | 47 | event/season switches (`EventDoubleXP`, `EventHalloween`, `Anniversary2020`, `IsPvPServer`, `IsTestLive`, `EventServer`), account gates (`Account_FreePlayerEffect`, `PvPBundleVendor`, `EnableVeteranVendor`), the AA unused-token caps, `MailTimeout` 30, `HackerTracker_Enabled` 1, and a handful of spell ids (`TWK_Combat_ActiveBlockingBuffID`, `TWK_WeaponSwapBuffClearTemplate`, `TWK_SpellToCastOnCrouchExit`, `TWK_PvPTournamentBuff`) |

**"Absent" is not a synonym for "server-side" in every case.** Five `TWK_*CombatText*` display constants
(velocity, time-to-live, stack distance, max entries, offsets) are in the absent set too, and a floating-number
renderer is plainly the client's job — so those read as **stale keys** for a consumer that no longer looks them
up here. What the test supports exactly is *no native client code names this key*; a `TweakPanel_c` /
`BalanceTweakView` debug panel still iterates the whole table generically, which is how a GM sees them all.

### The PvP family, in full — every one of them ours

Measured with the same test, plus `TweakGlobals` on the ones that ARE in the exe. **Every PvP tuning key is
either absent from the exe or registered with zero references**, `TWK_PvPCowardBuff` alone excepted (reads=1):

| key | value | what it settles |
|---|---:|---|
| `TWK_PvpDamagePercent` | 1 | PvP damage is **100%** of the PvE figure — there is no separate PvP damage scale to apply |
| `Combat_PvP{0..3}ShieldBonus` | 0.2 / 0 / -0.15 / -0.30 | the shield-rose arcs against a PLAYER defender |
| `Combat_PvPDamageReductionMainOnSplash` | 1 | a no-op under any reading, as the melee-cleave comment records |
| `TWK_PvpMaxAbsorbAmount` | 0.2 | the absorb cap in PvP |
| `PvPLevelExpModifier` | 0.05 | XP for a player kill |
| `PvPLootChance` / `PvPLootTable` | 0.25 / 1 | a player kill drops loot a quarter of the time |
| `PvPCashDropValue` / `PvPGenerateCashValue` | 0.05 / 325 | and cash |
| `DailyPvPKillsCap` | 20 | the same 20 the daily-kill counter 1000070 is clamped at, from a second source |
| `GuildRenown_PVPKillMultiplier` | 0.5 | a player kill feeds guild renown at half rate |
| `HardcorePvPXPModifier` | 1.5 | |
| `IsPvPServer` | 0 | the server-type switch — absent from the exe entirely |
| `TWK_PvPCowardBuff` | 3881658 | the one the client DOES read (reads=1) |

`GuildRenown_LevelingGainBase` 115 and `GuildRenown_LevelingGainMultiplier` 0.05 are the levelling half of the
same ladder, registered and unreferenced. What none of them states is the FORMULA the base and multiplier go
into, so nothing computes renown gain; open work tracked in Gitea #147.

## Out-of-combat regen: the attribute modifiers

The regen split we already implement — health and mana halt in combat, stamina does not — **is** this table's
`OOC` vs `Natural` distinction:

| tweak | value |
|---|---|
| `TWK_OOCHealthRegenConModifier` | 0.15 |
| `TWK_OOCHealthRegenStrModifier` | 0.15 |
| `TWK_OOCManaRegenIntModifier` | 0.38 |
| `TWK_OOCManaRegenWisModifier` | 0.38 |
| `TWK_OOCStaminaRegenConModifier` / `StrModifier` / `DexModifier` | 0.15 each |
| `TWK_NaturalManaRegenIntModifier` / `WisModifier` | 0.07 each |
| `TWK_NaturalStaminaRegenStrModifier` / `DexModifier` / `ConModifier` | 0.05 each |
| `Energy_RegenDelayTimer` | 1.0 |

So a pool's regen is driven by ATTRIBUTES, not by a fraction of its maximum. The full rates are **solved and
confirmed against retail's own wire values** — see [`docs/subsystems/player-stats.md`](../subsystems/player-stats.md)
for the formulas, the per-character verification and how the flat terms were pinned.

**A tweak's float lives at slot + 8.** The registration slot holds a POINTER to the tweak object, and the consuming
code names the float at **slot + 8** — so counting references to the slot alone reports a read tweak as never read.
For `TWK_OOCHealthRegenStrModifier` the slot is `0x02D24498` and the regen routine (`FUN_0111a520`) reads
`0x02D244A0`, which reads 0.15 in the running process. `TweakGlobals.java` counts both addresses; the string-table
test above does not depend on it at all.

The `Natural*` modifiers are the in-combat twins and are not solved: there is no `TWK_NaturalHealthRegen*` at
all, matching the sheet showing `Natural Health Regen = 0` for every captured character, but the mana/stamina ones
(0.07 / 0.05) have no captured delta to check against, since every captured `RegenIIR` is out of combat.

## Armour and protection targets

| tweak | value |
|---|---|
| `TWK_TargetPerLevelArmor` | 137.225 |
| `TWK_TargetPerLevelProtection` | 46.0625 |
| `TWK_ArmorAbilityModifier` | 2.0 |
| `TWK_ProtectionAbilityModifier` | 0.5 |

These are the per-level *targets* the itemization budget is scaled against, which is what template-scaled items
(the ones carrying no explicit `[2][9]` stat records) need.

## Spell / buff ids the SERVER is expected to own

The table names ids for effects a server has to apply, which is a shortcut for wiring them without a capture:

| tweak | id |
|---|---|
| `TWK_NanoExhaustedEffect` | 2960 |
| `TWK_StaggeringBuffID` | 4759888 |
| `TWK_Combat_ActiveBlockingBuffID` | 3826780 |
| `TWK_WeaponSwapBuffClearTemplate` | 3762746 |
| `TWK_SpellToCastOnCrouchExit` | 3794824 |
| `TWK_GC_DrowningSpell` | 295483 |
| `TWK_GM_Res_SpellID` | 4842692 |
| `TWK_PvPCowardBuff` | 3881658 |
| `TWK_PvPTournamentBuff` | 4800281 |
| `Character_SetupDefaultStatsScript` | 17581 |

`Character_SetupDefaultStatsScript` is the script the client names as the source of a character's DEFAULT stats —
the per-class/level curve `CharacterMaxStats.For` builds from RDB 1000511 (base + growth + attribute weights).

## Other constants worth knowing

`TWK_GlancingHitBaseChance` 5 · `TWK_InterruptChance` 30 · `TWK_InterruptModifier` 1.3 ·
`TWK_InterruptTimeModifier` 1.5 · `TWK_InterruptMinDuration` 0.5 · `TWK_OffhandDamagePercent` 0.5 ·
`TWK_BonusDamageCap` 0.5 · `TWK_OverwhelmingHitDamageBaseMultiplier` 20 · `TWK_ComboMaxCooldownTime` 10 ·
`TWK_SpellHate` 10 · `TWK_AttributeAdjustment` 0.4 · `TWK_EvadeRatingDexBonusModifier` 0.5 ·
`MaxMovementModifierRate` 1.85 · `Quest_MaxGetDistance` 5 · `AIDefaults_MaximumChaseRadius` 45 ·
`AIDefaults_ChaseDeaggroDelayTime` 5 · `Combat_PvE0ShieldBonus` 0.2 (PvE1 0.0, PvE2 −0.33, PvE3 −0.66).

**All three `AIDefaults_*` are adopted**: `WorldState.NpcLeashRange` is the sheet's 45, `NpcIrritationForgetSeconds`
its 4 and `NpcChaseDeaggroDelaySeconds` its 5. They are separate clocks from `NpcAntiWhittleSeconds` (10), which is
**ours**: the sheet says nothing about an anti-whittle rule, and the number has to cover the gap between two casts of
a slow spell so a heal cannot land mid-fight. One shared constant for engagement memory, hold-ground time and the
anti-whittle gate would couple three behaviours that retail tunes independently.

**Check this sheet before deciding a number is missing from the client.** Two whole subsystems' numbers live here:

- **Khitai faction ranks** — `FactionValueRank1..4` = 50,000 / 200,000 / 450,000 / 1,000,000, with
  `MaxFactionPoints` ±1,100,000 and a `Neutral{Min,Max}FactionPoints` band of ±5,000
  ([factions.md](../subsystems/factions.md)).
- **Daily challenges** — `DailyPvEKillsCap` 5, `DailyPvPKillsCap` 20, `DailyMinigameWinsCap` 1,
  `DailyQuestsCompletedCap` 3, `DailyUnchainedDungeonsCompletedCap` 1, and the meta caps `DailyMetaCap` 3 /
  `WeeklyMetaCap` 15 / `MonthlyMetaCap` 60.

  **Each cap has exactly one COUNTER stat, and the two families pair one-to-one. The counters are a contiguous run in the
  client's own stat-name registry (`docs/re/aoc-client-stat-names.tsv`):

  | counter stat | id | its cap | value |
  |---|---|---|---:|
  | `DailyPvEKills` | 1000069 | `DailyPvEKillsCap` | 5 |
  | `DailyPvPKills` | 1000070 | `DailyPvPKillsCap` | 20 |
  | `DailyMinigameWins` | 1000071 | `DailyMinigameWinsCap` | 1 |
  | `DailyQuestsCompleted` | 1000072 | `DailyQuestsCompletedCap` | 3 |
  | `DailyUnchainedDungeonsCompleted` | 1000073 | `DailyUnchainedDungeonsCompletedCap` | 1 |
  | `DailyDailyCounter` | 1000074 | `DailyMetaCap` | 3 |
  | `DailyWeeklyCounter` | 1000075 | `WeeklyMetaCap` | 15 |
  | `DailyMonthlyCounter` | 1000076 | `MonthlyMetaCap` | 60 |

  **A cap is paired with its counter by NAME; a cap is a daily-challenge COMPLETION target, not a clamp.** Crossing
  it fires the challenge-completion burst (see [daily-challenges.md](../subsystems/daily-challenges.md) and
  `--report daily-kills`):

  * **`1000070` is a witnessed-NPC-death counter, clamped at 20.** Where a capture's deaths stay under 20, its
    increments EQUAL the death count exactly (9=9, 10=10, 11=11, … on 7 of 15 captures); four captures run
    **29 / 34 / 37 / 39** deaths and the counter stops dead on **20** in every one (in
    `xxx_..._play1_beach-to-enter-tortage_city` it reaches 20 at frame 6039 and never moves again while the capture
    runs to frame 11692). That clamp is the SERVER's. It advances under a PvP name in a corpus with no PvP.
  * **`1000069` is the FATALITY count.** Each of its bumps pairs with the two Fatality self-buffs the server applies
    to the killer (5070797 + 791221): 49 of 49 bumps within twelve packets after a fatality apply on the player, 49
    of 49 applies followed by a bump, with the lifetime `1379 PvEFatalities` pushed in the same burst. It passes its
    stated cap of 5 and carries on (to **13** in the capture above) with no stall; crossing 5 is the daily fatality
    challenge's completion.

  The id↔name pairing is the client's own: its registration (`FUN_01df7df0`) writes `0xF4285` before naming
  `DailyPvEKills` and `0xF4286` before `DailyPvPKills`, and registers each id again under sub-indices 1/2/5/8 that
  carry no name. Besides the defaults registry `FUN_010CB540`, the client builds a global tweak accessor for
  `DailyPvPKillsCap` — a `SignalTarget` at `0x02D243D8`, constructed at `0x01DF61A0` inside the PvP-XP economy run
  of static initializers (`PvPXPFromPvPCapMultiplier`, `MinigamePvEXPMultiplier`, `PvEXPFromPvPGroupPenalty`,
  `TWK_MinLevelForPvPXP`) — and builds **none** for `DailyPvEKillsCap`. Nothing reads that global but its own ctor
  and its atexit, so it is not a second consumer; it does establish that the two caps are not interchangeable in
  the client, which is the shape the clamp asymmetry has.

  …plus `CounterDailyChallenges` (1000077) and the three RESET stamps the run opens with — `DailyResetTimestamp`
  (1000066), `WeeklyResetTimestamp` (1000067), `MonthlyResetTimestamp` (1000068). So "does it reset daily" is
  answered by the client carrying a per-character stamp for it, not only by the name.

  The meta ladder reads straight off the numbers: five capped activities feed a daily meta of **3**, which feeds
  **15** a week (5 days × 3) and **60** a month (4 weeks × 15).

  **Stat `106` is NOT one of these.** 106 counting 20→19→18 while `DailyPvPKills` counts 0→1→2 is
  `PowerSessionRemaining` (0x06A) being spent, a separate quota that happens
  to start at 20 as well; the captured snapshot ships `0x14` for it. Its own reset stat is
  `NextPowersessionReset` (105). Two systems, one coincidence of size.

**The exe registers a fallback for every tweak and the shipped sheet overrides them**, so the binary's
constants are not the game's numbers: all eight daily caps differ between the two. `FUN_010CB540` is the registry
(`name`, then a `double`); this table is what actually ships.

## The whole sheet, by family — so a number is looked UP, not looked for

The 294 names are not one flat list; they fall into families, and knowing the families is what makes this page a
lookup. `--report rdb-tweaks --report-arg <substring>` filters by name.

| family | count | what it covers |
|---|---:|---|
| `TWK_*` / `TKW_*` | 96 | the combat/regen/spell constants the sections above document |
| `Camera_*` | 31 | the third-person camera's path, zoom and tilt |
| `PvP_MonthlyFestival_*`, `WorldBoss_Spawn*` | 66 | event SCHEDULING slots — a spawn day/week per boss, all `0` on this sheet |
| `Onslaught_*` | 8 | the Onslaught wave scaling — `BarTargetBase` 30, `BarTargetPerLevel` 2, HP/damage per-wave scalars 1.0185 (boss 1.04 / 1.025) |
| `Combat_Pv[EP]{n}ShieldBonus` | 8 | the directional-shield bonus per difficulty tier: PvE `+0.2 / 0 / −0.33 / −0.66`, PvP `+0.2 / 0 / −0.15 / −0.3` |
| `AlternateAdvancement_*` | 6 | `XPCap` 1,000,000, the three unused-token caps at 100 each, `PvPModifier` 5, `SpecificTokensAwarded` 3, `GenericTokensAwarded` 1 |
| `Faction*` | 8 | the Khitai rank ladder — see [factions.md](../subsystems/factions.md) |
| `Daily*Cap`, `WeeklyMetaCap`, `MonthlyMetaCap` | 8 | the daily-challenge caps |
| `Quest_*` | 6 | the quest system's limits, below |
| `Inventory_*`, `ExpandInventories_*` | 5 | container sizing, below |
| `PvP*`, `Hardcore*`, `Arena*` | 10 | loot chance 0.25, `PvPCashDropValue` 0.05, `PvPGenerateCashValue` 325, `PvPLevelExpModifier` 0.05, `HardcorePvPXPModifier` 1.5, `IsPvPServer` 0 |
| `GuildRenown_*`, `Renown*` | 5 | `LevelingGainBase` 115, `LevelingGainMultiplier` 0.05, `PVPKillMultiplier` 0.5, `CacheLimit` 1000, `SaveInterval` 300 s |
| `Event*`, `Anniversary*`, `Saga*` | 13 | seasonal toggles, all off on this sheet |
| `AIDefaults_*` | 3 | `MaximumChaseRadius` 45, `ChaseDeaggroDelayTime` 5, `IrritationForgetPeriod` 4 |

### The one-offs worth knowing

| tweak | value | why it matters |
|---|---:|---|
| `Quest_MaxQuestPerPremiumPlayer` / `...FreePlayer` | 45 / 30 | the **journal cap** — a character can hold at most 45 quests, which is what bounds the journal region rather than any wire ceiling |
| `Quest_MaxGetDistance` | 5 | how close you must be to take a quest |
| `Quest_TimeToCompleteMultiplier` / `Quest_CooldownTimeoutMultiplier` | 60 / 60 | the quest record's time columns are in MINUTES, these convert to seconds |
| `Quest_LeaveBoundedAreaTimeout` | 10 | the grace period on stepping out of a `ReachArea` goal's volume |
| `Inventory_DisabledBankSlots` | 30 | of the bank's slots, how many start locked |
| `ExpandInventories_MaxBankSlots` | 300 | the bank's ceiling once expanded |
| `Inventory_DisabledResourceSlots` | 12 | the resource bag's locked slots |
| `RequiredTagForPFAccessTeleport` | **2063** | a concrete TAG id (the achievement tag space) — playfield-access teleport is gated on holding it |
| `Character_SetupDefaultStatsScript` | **17581** | the script the server runs to stat a NEW character |
| `Energy_RegenDelayTimer` | 1 | seconds of no spend before the energy pool refills |
| `MailTimeout` | 30 | mail's own timeout (days on retail's rules) |
| `MaxMovementModifierRate` | 1.85 | the ceiling on stacked movement buffs |
| `Achievements_Vistas_Enabled` / `Achievements_Lore_Enabled` | 1 / 1 | the vista and lore achievement subsystems are ON |
| `NPCsWithNoNormalsetsAllowed` | 0 | an NPC without a `normalset` is a content error, not a valid state |
| `GlobalModIncludedSpellLine` / `Excluded` | −1 / −1 | a global spell-line override, unused on this sheet |
| `WaterSplashTextureID` | 3728533 | an RDB id, i.e. the sheet also carries RESOURCE references |

Check this sheet before concluding a number is absent from the client.

## Sibling tables (same single-entry family, NOT this grammar)

Types 1000505–1000521 are all single-entry tables; only **1000516** uses the named `[len][name][f64]` shape. The
others are fixed-width numeric records. **1000511** is `CHARACTER_HP_MANA_STAMINA`, decoded below. **1000513** is a
DIFFERENT per-class/level curve, still undecoded: 960 records × 24 bytes = **12 classes × 80 levels**,
each `[u32 classId][u32 level][0][u32 value][0][u32 tag]`. Its class ids are exactly the 12 AoC classes
(18, 20, 22, 24, 28, 29, 31, 34, 39, 41, 43, 44) and its `value` column is **0 for all 80 levels of classes 24,
28, 41 and 44** — precisely the four classes with no stamina pool — while the other eight grow linearly with
level (class 34: 90 at L1 → 25082 at L80, ~316/level). Its L1 value for the Assassin is even 90, exactly the
`HpBaseL1` anchor — which makes it look like the vitals table. It is NOT: 56 does not divide its payload, and the
real one (1000511) reproduces the captured sheet exactly. Check a parser's record-size assert before believing a
shape.

## `CHARACTER_HP_MANA_STAMINA` = RDB type 1000511 — DECODED

One of the 25 named data tables (see the section above). The parser names itself and states its own record size,
which is what pins the format without guesswork — `HitsManaAndStamina_c::ReadFromRDB` (`Data\HitsManaAndStamina.cpp`,
`FUN_0113e220`) asserts:

```
((cData.GetRemainingSize() % (14 * sizeof(int32))) == 0)
```

### Grammar

```
[12-byte header (skipped)][i32 version][i32 count][count × 14 × i32]
```

`version` is 2 in the shipped file; the 14th field of each record is read only when `version > 1` (else 0). Type
1000511 is exactly `20 + 95 × 56` = 5340 bytes, which is what identifies it — no other RDB entry matches that shape.

Each record is keyed `[race][class]`, and all 95 are `5 races × 19 class slots` with every pair unique:

| field | meaning |
|---|---|
| 0 | race (1, 2, 3, 4, 10) |
| 1 | class — all 12 AoC class ids, plus 0–4 and 26/42 (archetype / unused slots) |
| 2, 3, 4 | **base** health, mana, stamina — ÷100 |
| 5, 6, 7 | **per-level growth** of the same three — ÷1000 |
| 8–13 | per-attribute-point weights for the three pools, in thousandths (8 is always 8000 = 8 health per Constitution; 9/10 are 0 or 3000; 11–13 are 0 or 2000) — see `CharacterMaxStats` |

The coefficients depend only on CLASS — except class 20 (Guardian), which has two race-dependent variants.

### The formula, verified byte-exact against four captured characters

```
value(level) = field/100 + (level - 1) × growth/1000
```

reproduces the imported snapshot sheet's stat **1** (Health), **507** (Mana) and **505** (Stamina) EXACTLY for every
captured character — at four different levels across four classes:

| character | level | class | formula (hp/mana/stam) | captured sheet 1/507/505 |
|---|---|---|---|---|
| AssassinL23 | 23 | Assassin | 772 / 0 / 736 | 772 / 0 / 736 |
| NecroL80 | 80 | Necromancer | 2371 / 2610 / 0 | 2371 / 2610 / 0 |
| DemoL80 | 80 | class 44 | 2371 / 2610 / 0 | 2371 / 2610 / 0 |
| Hakotep | 24 | class 43 | 839 / 930 / 414 | 839 / 930 / 414 |

### Where it is used

`CharacterMaxStats.ClassBaseHundredths` is this curve (in wire hundredths: `baseWord + growthWord × (level − 1) / 10`),
and the attribute weights above are its attribute term. Its L1 base health matches the retail-anchored `HpBaseL1` for
all 12 classes, and its mana/stamina bases are the displayed L1 values **minus 6** (the "mana base = displayed − 6"
that `SnapshotVitals` observes).

It is **not** the displayed maximum. The curve yields the sheet's `1`/`505`/`507`; the per-character values at
`27`/`504`/`506` are larger (NecroL80 6518 health vs the curve's 2371) and vary between two level-80 characters of
different classes: the client tops the class base up with the attribute term plus gear and feats. Using the class
base as the ceiling would LOWER every geared character's maximum; the ceiling is `CharacterMaxStats.For`.

Dump it with `--report rdb-dump --report-arg 1000511`.

## Where the armour constants land in the client

The mitigation routine `FUN_0113ba00` (log tag `GameCode/ArmorMitigation`) reads four globals that are **0.0 in
the image**, and `XrefsTo` finds only READS of them — no writer, which is the tell that a computed pointer fills
them at load. They are four of this table's constants, and their roles identify them one-to-one; the pairing is
forced by which branch of the routine uses which:

| global | role in `FUN_0113ba00` | tweak | value |
|---|---|---|---|
| `0x02d24704` | physical divisor — `mitigation% = adjusted / (level × C) × 100` | `TWK_TargetPerLevelArmor` | 137.225 |
| `0x02d24714` | magical divisor, same shape | `TWK_TargetPerLevelProtection` | 46.0625 |
| `0x02d24724` | magical ability weight — `(Int/Wis − 10) × 0.1 × C` | `TWK_ProtectionAbilityModifier` | 0.5 |
| `0x02d24734` | physical ability weight — `(Strength − 10) × 0.1 × C` | `TWK_ArmorAbilityModifier` | 2.0 |

`CombatResolver.MitigationFraction` computes with exactly these, which is why its trace points reproduce
retail's numbers — the formula is retail's, not a fit. Tools: `tools/ghidra/DumpFloatsAt.java` reads a global's
value (a 0.0 says "runtime-filled"), `tools/ghidra/XrefsTo.java` shows whether anything writes it.

