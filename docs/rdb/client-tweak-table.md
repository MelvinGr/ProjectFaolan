# The client's balance-tweak table (RDB type 1000516)

RDB type **1000516** is a single entry holding the game's own tuning sheet: **294 named `double` constants**
("tweakables"). It is the authority for numbers the server otherwise has to guess — regen rates, armour targets,
combat chances — and for a set of spell/buff ids the server is expected to cast itself.

Dump it with:

```zsh
dotnet run --project Faolan.Importer -- --report rdb-tweaks --RdbDir <rdb-dir>
dotnet run --project Faolan.Importer -- --report rdb-tweaks --report-arg regen --RdbDir <rdb-dir>   # name filter
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

⚠️ **`TweakGlobals.java` reports these three as "CLIENT NEVER READS IT", and that verdict is a false negative** —
the client reads all of them. The script reports references to the registration slot, which holds a POINTER to the
tweak object; the float lives at **slot + 8**, and that is the address the consuming code names. For
`TWK_OOCHealthRegenStrModifier` the slot is `0x02D24498` and the regen routine (`FUN_0111a520`) reads `0x02D244A0`,
which reads 0.15 in the running process. So a "never read" verdict from that script means *check slot+8 before
concluding anything* — several tweaks that look server-only are not.

The `Natural*` modifiers are the in-combat twins and are NOT yet solved: there is no `TWK_NaturalHealthRegen*` at
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
(the ones carrying no explicit `[2][9]` stat records) need — open work tracked in Gitea #441.

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

Note `AIDefaults_MaximumChaseRadius` = 45 and `AIDefaults_ChaseDeaggroDelayTime` = 5: our NPC chase/leash values
are tunable guesses and this is what retail used.

## Sibling tables (same single-entry family, NOT this grammar)

Types 1000505–1000521 are all single-entry tables; only **1000516** uses the named `[len][name][f64]` shape. The
others are fixed-width numeric records. **1000511** is `CHARACTER_HP_MANA_STAMINA`, decoded below. **1000513** is a
DIFFERENT per-class/level curve, still undecoded: 960 records × 24 bytes = **12 classes × 80 levels**,
each `[u32 classId][u32 level][0][u32 value][0][u32 tag]`. Its class ids are exactly the 12 AoC classes
(18, 20, 22, 24, 28, 29, 31, 34, 39, 41, 43, 44) and its `value` column is **0 for all 80 levels of classes 24,
28, 41 and 44** — precisely the four classes with no stamina pool — while the other eight grow linearly with
level (class 34: 90 at L1 → 25082 at L80, ~316/level). Its L1 value for the Assassin is even 90, exactly the
`HpBaseL1` anchor — which is why it looked like the vitals table. It is NOT: 56 does not divide its payload, and the
real one (1000511) reproduces the captured sheet exactly. Check a parser's record-size assert before believing a
shape; that is what disproved this one.

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
| 8–13 | further per-class constants (8 is always 8000; 9/10 are 0 or 3000; 11–13 are 0 or 2000) |

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

### What it fixes, and what it does NOT

It settles the **base curve** `CharacterMaxStats.Archetype` only guessed at. Its L1 base health matches our
retail-anchored `HpBaseL1` for all 12 classes, and its mana/stamina bases are our displayed L1 values **minus 6** —
independently confirming `SnapshotVitals`'s "mana base = displayed − 6". But the **per-level growth we invented is
wrong across the board**, e.g. (ours → real) Guardian hp 60 → 40, Assassin hp 53 → 31, Priest of Mitra hp 45 → 10,
Bear Shaman stamina 39 → 17.

⚠️ It is **not** the displayed maximum. The curve yields the sheet's `1`/`505`/`507`; the per-character values at
`27`/`504`/`506` are larger (NecroL80 6518 health vs the curve's 2371) and vary between two level-80 characters of
different classes, so the remaining factor is per-character — gear, which is Gitea #441. Wiring this curve straight
into `CharacterMaxStats` would LOWER a geared character's maximum, so the mapping needs #441 first. Open work: #3.

Dump it with `--report rdb-dump --report-arg 1000511`.

## Where the armour constants land in the client (2026-07-28, #401)

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

`CombatResolver.MitigationFraction` already computes with exactly these, which is why its trace points reproduce
retail's numbers — the formula is retail's, not a fit. Tools: `tools/ghidra/DumpFloatsAt.java` reads a global's
value (a 0.0 says "runtime-filled"), `tools/ghidra/XrefsTo.java` shows whether anything writes it.

