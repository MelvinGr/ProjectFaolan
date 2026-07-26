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

So a pool's regen is driven by ATTRIBUTES, not by a fraction of its maximum. The exact combination is not yet
pinned: for NecroL80 (L80, Con 375, Str 70) the client's Physical tab shows **Non Combat Health Regen = 86**, while
`0.15 × (Con + Str)` = 66.75 — the ingredients are right, a term is missing. A second character's Physical tab
gives the second equation needed to solve it (open work tracked in Gitea #442).

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
the per-class/level curve our `CharacterMaxStats.CreateDefaults` currently approximates from L1 anchors only.

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
others are fixed-width numeric records, e.g. **1000513** = 960 records × 24 bytes = **12 classes × 80 levels**,
each `[u32 classId][u32 level][0][u32 value][0][u32 tag]`. Its class ids are exactly the 12 AoC classes
(18, 20, 22, 24, 28, 29, 31, 34, 39, 41, 43, 44) and its `value` column is **0 for all 80 levels of classes 24,
28, 41 and 44** — precisely the four classes with no stamina pool — while the other eight grow linearly with
level (class 34: 90 at L1 → 25082 at L80, ~316/level). The column is therefore stamina-gated, but its scale does
not match any stored value yet, so it stays undecoded.
