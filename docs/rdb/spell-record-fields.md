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
splash triple. All three are now imported under their own names — `Spell.SplashRadius` (347 ÷ 100, metres),
`Spell.SplashDamage` (348 ÷ 100, percent) and `Spell.SplashMaxTargets` (349, a raw count) — and `Faolan.Web`
labels them as such. Nothing in combat reads them; the spell's real damage magnitude stays unsourced (#592).

## ⚠️ Field 54 is not the level requirement — 125 is

Both ids are called `Level` somewhere, and 54 is on **20,186** records to 125's **7,645**, so the narrow one
reads like an import that missed most of the catalog. It is the other way round.

Field 54 takes 23 values over the whole catalog and **19,957 of them are `1` or `80`** — a content BAND
(pre- vs post-cap), not a per-spell requirement. Field 125 takes 43 values and tracks the unlock ladder every
other source agrees with: `Gate of Hell` 70, every rank-6 spell 80, and `Ice Strike`'s six ranks
7 / 20 / 35 / 50 / 65 / 80. `RdbSpellImporter` reads 125, and that is correct.

## Field 132 `ManaCost` is usually a PERCENTAGE of the caster's mana pool

Read straight, 132's values look too small to be a cost: `Ice Strike (Rank 6)` carries **9**. They are
percentages, and the client says so. `FUN_008d6f70` — the function behind the spell tooltip's
`ManaCostToolTip` line — is:

```c
cost = spell.GetIntStat(0x84);                               // this field, clamped to >= 0
if (spellFlags & 4)                                          // the percent-of-pool mode
    cost = (int)(caster.GetIntStat(0x1FB) * cost * 0.01f);   // 0x1FB = ManaMax, wire units
cost = (int)((modifier.Amount * 0.0001f + 1.0f) * (modifier.Value + cost));
```

and the tooltip prints `cost * 0.01`, dividing the wire's hundredths back to display units. The two modifier
terms are exactly the [`FeatSpellStatModifier`](../subsystems/feats.md) pair — an absolute `Value` plus an
`Amount` in hundredths of a percent — that feats already target at `ManaCost`, which is what says this is the
real cast-cost path rather than a display-only accessor.

**The flag word selects a second, FLAT mode**, and both modes carry real player spells:

| spell | field 132 | reading |
|---|---:|---|
| `Ice Strike` ranks 1-6 | 8, 9, 9, 9, 9, 9 | ~9% of the pool at every rank |
| `Dark Knowledge` rank 1 | 10 | 10% of the pool |
| `Dark Knowledge` ranks 2-4 | 12800, 26500, 42200 | 128 / 265 / 422 mana flat |

One spell LINE mixing the two rules out "flat is a pre-launch leftover". Which RDB field feeds the flag is
**not decoded** (Gitea #630), so `CombatResolver.ResolveSpellManaCost` picks the mode by magnitude; the data is
sharply bimodal, with **1,209** spells at 1..100, **30** at 101..999 and **452** at 1000+.

`Spell.ManaCost` carries the field raw, both modes in the one column. 1,691 of the 20,847 catalogued spells
carry a cost; a 0 (or an absent field) means the spell is free, which is retail's own reading — the client's
property accessor returns 0 for a record that carries neither.

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
that id — but what the property MEANS on a spell still has to be checked against values, and `ManaCost` is the
worked example: its name is right and reading it as an absolute cost would still be wrong (see above).
Some do read straight through (`StaminaCurrent` 7700 on 398 records is a stamina cost;
`SecondaryMagicRating`'s values 932–937 are themselves the `MagicRatingFire..Poison` stat ids). Others are
Anarchy-Online inheritance wearing a stat id — `AwardedOmniTekTokens` on 8,505 spell records is a ladder of
1,1,2,2,3,3,4,4, which is a rank, not a token award.

## The mount catalog falls out of it

Field **186 `Mount`** occurs on **155** records, 122 distinct values — every mount summon spell in the game,
each naming its mount creature id. `MountCatalog`'s hand-built entries were a two-row slice of it: the
`Fading Black Horse` id `0x3E34F1` is RDB spell **4076785**, whose field 186 is 3226779.

All 155 are imported as the `Mounts` table — see [`../subsystems/mounts.md`](../subsystems/mounts.md), which
also settles what separates the `Swift` / `Azure-barded` variants of a horse (field **499**, the same
quality/rarity ladder an item template carries — *not* a speed).

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
