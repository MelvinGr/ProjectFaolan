# The spell record's property fields ARE stat ids

RDB type **1040005** (the spell catalog, 21,502 records) is a 12-byte header followed by a run of
`[fieldId u32][value u32]` LE pairs in strictly INCREASING field order. 111 distinct field ids occur.

**The field ids are the same id space as wire stat ids** — `CharacterStatId` names them. That is what turns the
field run from a numbered blob into a readable property bag: **45 of the 111** already have a client name, most
of them out of the `GameAction_*` / LDB-615 runs.

## Why this is not circular

The names come from two sources that never looked at this record:

- **LDB category 615**, the client's own stat-name table, reached through the feat `SpellStatModifier` /
  `SpellLineStatModifier` records (#593) — those modifiers target a SPELL's properties through the stat id
  space, which is the direct statement that the two spaces are one.
- **The client's static stat-name metadata table** (`docs/re/aoc-client-stat-names.tsv`), recovered from
  `AgeOfConan.exe`, for everything at 768 and above.

Six field meanings had already been established empirically from the RDB alone, and the names agree with every
one of them:

| field | established empirically as | client's name for that id |
|---|---|---|
| 8 | duration, centiseconds | `Duration` |
| 79 | icon → an RDB 1010008 PNG id | `Icon` |
| 287 | range × 100 | `AttackRange` |
| 294 | cast time, centiseconds | `CastingTimeSeconds` |
| 347 | splash radius × 100 | `SplashRadius` |
| 349 | splash max targets | `SplashDamageMaxTargets` |

A seventh agrees from the value side rather than the name side: field **847** `AttackAttributeBonus` holds
`808` = `Intelligence` on 482 records — a stat id as its VALUE, which only parses if the space is shared
(`spell-scaling.md`).

## ⚠️ Field 348 is SPLASH damage, not the spell's damage rating

The client names `0x15C` **`SplashDamage`**, and the data says the same thing: 348 occurs on **215** of the
21,502 records, exactly as many as 349 `SplashDamageMaxTargets`, and nested inside 347 `SplashRadius`'s 231.
Cross-tabulating 347 against 348 pairs them row for row — 124 records carry `347=0` and `348=0` together.

A property of one percent of the catalog is not "the spell's damage rating"; it is the third member of the
splash triple. `Spell.DamageRating` imports this field and `Faolan.Web` displays it under the wrong label —
open work is tracked in Gitea #626.

## The named fields

`count` is how many of the 21,502 records carry the field.

| field | hex | count | client name |
|---|---|---|---|
| 1 | 0x001 | 26 | `HealthMax` |
| 2 | 0x002 | 19 | `VolumeMass` |
| 8 | 0x008 | 11780 | `Duration` |
| 12 | 0x00C | 2 | `Mesh` |
| 21 | 0x015 | 1 | `BCC` |
| 23 | 0x017 | 1190 | `StaticInstance` |
| 27 | 0x01B | 22 | `HealthCurrent` |
| 54 | 0x036 | 19519 | `Level` |
| 75 | 0x04B | 8505 | `AwardedOmniTekTokens` |
| 79 | 0x04F | 15421 | `Icon` |
| 100 | 0x064 | 4 | `RespecPoints` |
| 129 | 0x081 | 1228 | `SpellCriticalDamage` |
| 132 | 0x084 | 5264 | `ManaCost` |
| 147 | 0x093 | 1224 | `SpellCriticalChance` |
| 186 | 0x0BA | 155 | `Mount` |
| 222 | 0x0DE | 128 | `SpellColumnRadius` |
| 269 | 0x10D | 1 | `GatherSound` |
| 287 | 0x11F | 9965 | `AttackRange` |
| 294 | 0x126 | 12467 | `CastingTimeSeconds` |
| 300 | 0x12C | 1 | `Members` |
| 326 | 0x146 | 2 | `Immunity` |
| 347 | 0x15B | 231 | `SplashRadius` |
| 348 | 0x15C | 215 | `SplashDamage` |
| 349 | 0x15D | 215 | `SplashDamageMaxTargets` |
| 375 | 0x177 | 224 | `FatalityChance` |
| 380 | 0x17C | 507 | `SpellFatalityChance` |
| 385 | 0x181 | 61 | `ChanceOfBreakOnSpellAttack` |
| 387 | 0x183 | 162 | `HealthBreakThreshold` |
| 422 | 0x1A6 | 60 | `ChanceOfBreakOnAttack` |
| 447 | 0x1BF | 516 | `ChannelingInterval` |
| 475 | 0x1DB | 1 | `FatalityRating` |
| 504 | 0x1F8 | 398 | `StaminaCurrent` |
| 505 | 0x1F9 | 1 | `StaminaMax` |
| 506 | 0x1FA | 6 | `ManaCurrent` |
| 507 | 0x1FB | 1 | `ManaMax` |
| 536 | 0x218 | 1 | `MagicDamageModifier` |
| 803 | 0x323 | 224 | `SpellInturruptionModifierPercentage` |
| 844 | 0x34C | 595 | `SecondaryMagicRating` |
| 847 | 0x34F | 712 | `AttackAttributeBonus` |
| 853 | 0x355 | 1 | `SpellMagicModifier` |
| 857 | 0x359 | 9 | `SecondaryAttackRating` |
| 1109 | 0x455 | 3 | `EffectPackage` |
| 1200 | 0x4B0 | 3 | `Max` |
| 1289 | 0x509 | 1 | `ArtistryRenownModifier` |
| 1291 | 0x50B | 12 | `SpellLine2` |

⚠️ **A name is evidence, not a citation.** The id space is shared, so a name is genuinely the client's name for
that id — but what the property MEANS on a spell still has to be checked against values. Some read straight
through (`ManaCost` values 1500–6000; `StaminaCurrent` 7700 on 398 records is a stamina cost;
`SecondaryMagicRating`'s values 932–937 are themselves the `MagicRatingFire..Poison` stat ids). Others are
Anarchy-Online inheritance wearing a stat id — `AwardedOmniTekTokens` on 8,505 spell records is a ladder of
1,1,2,2,3,3,4,4, which is a rank, not a token award.

## The mount catalog falls out of it

Field **186 `Mount`** occurs on **155** records, 122 distinct values — every mount summon spell in the game,
each naming its mount creature id. `MountCatalog`'s hand-built entries are a two-row slice of it: the catalog's
`Fading Black Horse` id `0x3E34F1` is RDB spell **4076785**, whose field 186 is 3226779.

## Reproducing it

```zsh
# every field id, with the client's name for it
dotnet run --project Faolan.Importer -- --report spell-fields --RdbDir <rdb-dir>

# one spell's whole property run
dotnet run --project Faolan.Importer -- --report spell-fields --report-arg 4076785 --RdbDir <rdb-dir>

# one field's value distribution, optionally crossed with another
dotnet run --project Faolan.Importer -- --report rdb-field-coverage --report-arg 1040005:186 --RdbDir <rdb-dir>
dotnet run --project Faolan.Importer -- --report rdb-field-coverage --report-arg 1040005:347x348 --RdbDir <rdb-dir>
```
