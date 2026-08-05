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

## ⚠️ The STACKING family — and two of them were columns we shipped under the wrong name

LDB 615 names a whole contiguous family, and it reframed two shipped columns (renamed in #634):

| field | client's name | what we call it |
|---|---|---|
| 123 | `GUI Stacking Order` | `Spell.GuiStackingOrder` (renamed from Rank) |
| 128 | `GUI Stacking Line` | `Spell.GuiStackingLine` (renamed from LineId) |
| 546-550 | `Stacking Line 2` … `Stacking Line 6` | unread |
| 551 | `Stacking Order` | unread (was labelled "a rank-scaling magnitude") |

So a spell sits on up to six stacking LINES and carries an ORDER within them; a higher order supersedes a
lower one on the same line, which is how an MMO stops two +Strength buffs from stacking.

**This dissolves two mysteries these pages recorded as open.**

*Field 123 is not the rank.* It correlates with rank — a higher rank stacks over a lower one — which is why it
read as one. But an order only has to be MONOTONE within its own line, and the scale is otherwise free. Of the
883 spells with a `(Rank N)` suffix, 669 carry 1/2/3 and 214 a clean ×10: `Healing Benediction` is 10/20/30 on
stacking line **2**, `Wave of Life` is 1/2/3 on lines 24020+. Both are valid orderings. Nothing "discriminates
the two scales" because neither is a rank.

*Field 128's "far too coarse to be a line" small values are the point.* `Healing Benediction`'s line is `2` —
shared with dozens of unrelated spells, which is exactly what a shared stacking line is for. And an id whose
`/1000` is not a class (`50000`, `440061`) is just a stacking line that is not per-class. The feat join is
sound throughout: a feat boosting a "spell line" is boosting a stacking line.

The client's own dev test spells confirm the reading outright — `Strength Buff 1` / `Strength Buff 2` carry
551 = 1 / 2, and the same 1/2 pairing runs through `Dexterity Buff`, `Stack Debuff`, `Hostile` and `Buff`.

The two columns were renamed to the client's names in #634. The feat side keeps `SpellLineId`: it is the same id
space, but that name is the CLIENT's too — the GameActions that fill it are `AddSpellLineProc`,
`SpellLineStatModifier` and `ChangeLineHitPercent`, so renaming it would make the column disagree with the wire
record it is harvested from. Fields 546-551 are still unread; tracked with the rest of the unread run in Gitea
#633.

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

One spell LINE mixing the two rules out "flat is a pre-launch leftover".

### The flag is word 13 of the record, and it is not a property field

The selector is **bit 2 of the u32 at body byte 52** — the record's flags word, which is what the client keeps
at `spell + 0xC8`. Set = percent mode, clear = flat. Imported as `Spell.ManaCostIsPercent`.

Finding it meant looking OUTSIDE the property table, and the negative result is the useful part: three
independent sweeps over the ascending run — every field's value buckets, every single BIT of every field, and
each field's mere PRESENCE — all failed to separate the two modes (`--report spell-mana-mode`). That is what
said to look elsewhere. **The ascending run is only ~16 fields wide and starts at word 15, so roughly 80% of a
964-byte spell record is not property table at all**; word 13 sits in the fixed struct ahead of it.

Bit 2 of word 13 is set on **995** of the catalog's percent-mode spells and on **zero** flat-mode ones, it is
bit 2 exactly as the client's `>> 2 & 1` says, and it settles `Dark Knowledge`: rank 1 has it set, ranks 2-4
clear.

⚠️ It disagrees with the old "≤ 100 is a percent" heuristic on **215 of 1,713** records, and the flag is right
on every one: they are tiny FLAT costs — mostly a literal `1`, i.e. 0.01 mana — plus the catalog's test spells,
which the magnitude rule read as 1% costs. The import independently confirms the split: no percent-mode spell
exceeds **75**, where flat mode spans 1..65500.

`Spell.ManaCost` carries the field raw, both modes in the one column, with `ManaCostIsPercent` saying which.
1,691 of the 20,847 catalogued spells carry a cost (994 percent, 697 flat); a 0 (or an absent field) means the
spell is free, which is retail's own reading — the client's property accessor returns 0 for a record that
carries neither.

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

## Field 447 `ChannelingInterval` — a name confirmed by a number measured years apart

Imported as `Spell.ChannelingIntervalMs` (centiseconds ×10, like `Duration` and `CastingTimeSeconds`), and it is
the rare case where a client name can be checked against evidence that never looked at it: `Spell.DotIntervalMs`
was MEASURED from the gaps between captured `StatDamageIIR` ticks. On every spell where a record carries 447 and
a same-named record has a measured interval, the two agree **exactly** — Deathly Blizzard 2,500 ms, Waves of
Flame 1,000 ms, Resting 500 ms — with no disagreement. Gated by `SpellAreaAndChannelTests`.

That also settles the unit: 447's raw `250` on Deathly Blizzard is 2.5 s, not 250 ms.

## ⚠️ Field 210 `Recharge Delay` is NOT the spell's recast

It reads like the recast — a time, on 3,567 records, named `Recharge Delay` — and #633 listed it as "⭐ the
spell COOLDOWN". It is not. `Spell.RecastCentiseconds` comes from the record's own `SetSpellRecastTime` /
`SetSpellLineRecastTime` GameAction elements (`spell-recast-source.md`), and joining the two per record over the
whole catalog says they are different numbers:

| | records |
|---|---:|
| carry field 210 | 3,567 (2,334 of them `0`) |
| 210 > 0 **and** a recast > 0 | 768 |
| …of which the two are **equal** | **98** |
| 210 > 0, no recast | 465 |
| 210 = 0, recast > 0 | 178 |

98 of 768 is not a second source; it is what two independent round-number timers (100/1000/1500/2000/6000)
collide on by accident. Splitting by magnitude does not rescue it either — the ≥1000 bucket, which is almost
entirely NPC ability records (`Counterstrike III`, `Molten Steel Slash V`), agrees on only 91 of 259. And it is
not a restatement of any other timing column we already hold: against `CastTime` 6 of 826 agree, against
`DurationMs` 4 of 453, against `ChannelingIntervalMs` and `DotIntervalMs` 0.

**The unit IS settled**, which is what #633 asked for before importing: stat `0x0D2` sits in the client's
first percent-divisor set (`StatDisplayScale.Percent1`), so the client draws this field as *value ÷ 100* — the
same centiseconds every other time field in this run uses. `100` is 1 s, `10` is 0.1 s, `6000` is 60 s, and the
`500000` / `999999` / `99999999` outliers on 8 records are a "never" sentinel a recast would have no use for.

What it actually times is not settled. The population it sits on is suggestive rather than conclusive: at 0.1 s
it is `Ignite` / `Burning` / `Poisoned` / `Stunned` / `Ice Barrier` / `Omega Shield`, at 0.5 s the `Druid Heal
25..80` and `Energy Drain` tiers, at 2 s `Conqueror Evade 20..80`, the regen auras and the `Formation:` /
`Battle Cry:` band — periodic and *triggered* effects rather than casts, which fits an internal re-fire
throttle on a proc. That is a hypothesis with no client evidence behind it, so nothing imports the column.

## The AoE blast radius is NOT in this run — it is a GameAction element key

Three sweeps of the field run looked for a per-spell AoE radius and found none, and that reading was correct
about *this table*: the answer is one region over. The `[2][9]` element region carries the client's
area-TARGETING actions, and each states its own `Radius` (key 11 = stat `0x2DD`):

```
1136242 "Waves of Flame (Rank 1)":  53259 ConeFormula  88=1136241 212=-45 213=90 11=5
 144050 "Call of the Bee Hive":     53087 AreaFormula  88=143900 11=5
```

Key 88 is the sub-spell applied to everyone found inside the radius, so the element IS the AoE: an area, and what
to do to whatever is in it. Imported as `Spell.AreaRadius` (metres) over
`GameActionTypes.AreaTargetKinds` — a deliberate subset of the 17 types carrying key 11, because a radius is only
a blast radius when the element is selecting targets (an `AudioEffect`'s is a sound falloff, an
`AOERadiusModification`'s adjusts someone else's). 1,023 spells carry one. Dump a record's elements with
`--report spell-template --report-arg <id>`.

⚠️ Do not read `SplashRadius` (347) as this. Splash is the collateral a SINGLE-target hit spills onto bystanders;
this is the area the spell targets to begin with. They are different fields, different units (metres vs metres
×100) and a few spells carry both.
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

# ...and with --out, EVERY carrying record rather than the 14 samples (id / name / value, one TSV per field) -
# what a histogram cannot answer is "is this the second source for a column we already fill", and that needs the
# per-record join (#633, field 210).
dotnet run --project Faolan.Importer -- --report rdb-field-coverage --report-arg 1040005:210 --RdbDir <rdb-dir> --out <dir>
```
