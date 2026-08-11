# Spell stat effects: the `[2][9]` region of a spell template (#367)

A spell's RDB template (type `1040005`) carries the same `[2][9]` stat-modifier region item templates do, parsed
by the same code (`ItemTemplateStatModifiers`, see [item-template-stats.md](item-template-stats.md)) and imported
per spell as `SpellStatEffect` rows — one row per ELEMENT.

⚠️ **Do not hand-count this table.** Both the row total and the kind census have been restated from a manual read
before and gone stale by a wide margin — `--report spell-effect-gap` re-derives them from the shipped database and
asks the live engines which rows they apply, so it cannot report a policy the server does not have.

The record is ten `u32` LE words:

```
[kind][0][4][0][1][0][2][9][statId][amount]
          └────── signature ──────┘
```

`statId` is a wire stat id - the ids named by the client's own table, see
[ldb-stat-names.md](../re/ldb-stat-names.md). What makes the spell region different from the item one is `kind`,
the word that OPENS the record: item templates use one kind throughout, spells mix several, and **the kind says
how to read `amount`**.

## The kinds

| kind | reading | evidence |
| --- | --- | --- |
| 53045 | **flat** addition, in the stat's own wire scale | Fend `0x1C0`=1000 / `0x1C3`=500 (Armor/Protection, the same units a ring uses); Battle Cry: Marshal the Ranks `0x136`=44/62/80 across its rank ladder; a regen formation's `0x1F2`=500 → +5/s, matching the item-tooltip-proven ×100 |
| 53220, 53221 | a **LEVEL RAMP** in the stat's own wire scale - `Amount` is only its LOW endpoint | the record carries four slots, not one: `lerp(174 @ 172, 175 @ 173)` over the carrier's level, decompiled from the client's apply ([gameactionlist-grammar.md](../re/gameactionlist-grammar.md)). `Runed Flesh`'s ranks tile the level line 25-40 / 40-55 / 55-70 / 70-80 with values continuous across each seam. Carried as `SpellStatEffect.HighAmount` / `LowLevel` / `HighLevel` and resolved by `LevelRamp.ValueAt` |
| 53067 | **percent × 100** | Philtre of Guile `0x138`=200, Refined Philtre of Guile `0x138`=500 - and the client's own elixir descriptions read "boost the Character's total Critical Rating by an additional **2%** / **5%**". The same 2/5 and 10/20 tiering recurs across every 53067 consumable |
| 53068 | the **percent twin of the ramp** - percent × 100 at both endpoints | `InterpolatedModifyAsRating` is to 53067 what 53220 is to 53045: same four ramp slots, percentage units. One shipped row, on a dev test spell, and its element is too short to read the ramp from - imported but never honored |
| 53219, 53002, 53007, 53013 | an **instant pool move**, not a buff stat | health, mana or stamina at the ×100 vitals scale: Lifestrike R1-R5 = 3400…33700 (an instant heal, `DurationMs` 0), and the `-100000000` drain/kill sentinel on non-buff spells. Applied by `SpellPoolEffects` (#702), not by the stat engine |
| 53002, 53196, 53073, 53185 | a **RANGE**, not a number - `Amount` is its lower endpoint and `MaxAmount` its upper one | the key list is `[Stat, 2 min, 37 max, 71]`, and the client's own tooltip builder renders both (#782). `Lifestrike (Rank 1)` strikes for `-410 … -500` |
| 53219 | a **PAIR of level ramps** - a range whose both endpoints ramp over the same two levels | `[Stat, 168, 170, 172, 169, 171, 173, 71]`: the min ramps `168 @172 → 169 @173` and the max `170 → 171`. `Gangrenous Stench`'s tick states `-5840…-7140` at level 75 and `-6220…-7610` at 80, which the in-client tooltip draws as "58.40-71.40 at level 75 … 62.20-76.10 at level 80". Carried as `Amount`/`MaxAmount`/`HighAmount`/`MaxHighAmount` + `LowLevel`/`HighLevel` (#783) |
| 53110 | **flat**, exactly like 53045 — `Modify` with a duration in its name | its key list is `[0 Stat, 39 Value]`, character-for-character 53045's; its own ramped twin 53221 was already classified flat; and on stats whose unit is settled its amounts land in THAT unit — `Skin of Iron` carries the same +10000 = +100% step on four schools that `Magic Shield` carries with 53045 on cold, and `Soul of Mitra` (*"shielding the priest from almost all damage"*) carries 90 damage deflection beside `Phoenix Cloak`'s 53045 +40. Its bulk is a POLYMORPH tail — Head Mesh, Monster Data, Scale — which the STAT gate holds, not this one |

Attributes are the one place the spell and item regions disagree on SCALE: a spell stores **display units**
(`Potion of Strength (4h)` carries `0x324`=2, and the client's description for the sibling `0x32E` records reads
"Augmente la Dextérité de 1/2"), while an item stores the same attribute ×10.

## ⭐ The POLYMORPH family — what its values are, and what the wire carries (#709)

**296 rows over 231 spells** set appearance fields rather than combat ones — `Magic Morph Wolf`, `Fox Lady
ShapeShift`, `Ursine Bond`, `Morph : Acheronian Soul`, `Avatar of Destruction`. `--report spell-effect-gap
--report-arg 53110` lists them. Two questions had to be answered before any of it could be applied, and both
now are.

### The values are ids we already resolve

| stat | distinct values | every one resolves as |
|---|---:|---|
| 359 `Monster Data` | 21 | **RDB 1010201** (MonsterData) — 21 of 21 |
| 64 `Head Mesh` | 17 non-zero | **RDB 1010201** — 17 of 17, ⚠️ *not* 1010001 |
| 21 `BCC` | 3 | **RDB 1010203 + 1010501** — all three, including the two low ones |

⚠️ **`Head Mesh` does not hold a mesh.** Its values are MonsterData records, the same type as `Monster Data`'s
— which is exactly the shape `preview.bin` uses for a head: `<previewresource attractortype="33" name="Head"
resourceid="…" resourcetype="1010201"/>` (see [`client-settings.md`](../subsystems/client-settings.md)). Two
independent decodes, one from a spell effect and one from a client-authored file, agreeing that a head is a
1010201 attachment.

⚠️ And **the two low `BCC` values are not a different phenomenon.** `Grizzly Hide` / `Avatar of Destruction`
carry `10237` / `12488` where the true morphs carry `3986836`, which read as a magnitude beside a record id —
but all three resolve as the same type pair, so they are record ids too and `12488` is simply a low one. (It is
also the constant `NpcStatCore.Bcc` on every captured NPC.)

`Scale` (360) runs 50..300 and is a PERCENT of normal size, the same quantity as `NpcStatCore.MonsterScale`.

⭐ **So a polymorph is "wear this NPC's appearance columns".** Every field it sets is one we already hold on
`NpcStatCore`: `MonsterData`, `HeadMesh`, `MonsterScale`, `Race`, `SuperFamily`, `Bcc`, `Radius`, `Side`.
Nothing here needs decoding; it needs an applier.

### ⭐ There is no wire form: the CLIENT executes a polymorph itself

`--report morph-stats` censuses all **48,181** captured `StatIIR` (0xF98E10B3) records — the message that sets
ONE stat to ONE value on ONE entity — against the shapeshift set, and comes back nearly empty:

| stat | in the corpus |
|---|---|
| 360 `Scale` | 103 records, entity class `0xC350` (Character), only ever the two values 70 and 80 |
| 359 `Monster Data` | 4, on a `SimpleDynel`, all **zero** |
| 21, 33, 47, 64, 89, 121, 421 | absent |

That reads as "retail barely uses this channel", and the reason is that **retail does not use a channel at
all**. Every one of these records is a `GameAction` ELEMENT of the spell, and the client has an executor for
each kind — one C++ class per `e_Type_GC_GameAction_*`, an 11-slot vtable whose **slot 9 applies** the element
to its target and **slot 10 removes** it again. The whole table is
[`gameaction-executors.tsv`](../re/gameaction-executors.tsv) (`tools/ghidra/DumpGameActionExecutors.java`);
93 of the 214 kinds act client-side, and 121 are bare `return 1` stubs, which is exactly how a
server-authoritative kind looks (`Hit`, `DPSHit`, `HitPercentage`, `BindAtLocation` are all stubs).

Two of the client-side ones are this family:

```
53110 TempChangeSkill  apply  SetStatOverride(statTable, key0 Stat, key39 Value)      FUN_008a7a90
                       remove ClearStatOverride(statTable, key0 Stat)                 FUN_008a7ae0
53028 PolyMorph        apply  entry = character->PolyMorphHolder.Add()                FUN_008b5d50
                              entry = (key799 BodyCatMesh, key761 Value, key796 Flags)
                              UpdatePolyMorph(holder)
                       remove drop the entry by its token, UpdatePolyMorph again      FUN_008b5e20
```

`UpdatePolyMorph` (`FUN_00747dc0`, whose own assert string — *"UpdatePolyMorph run with no polymorph active.
Programming error."*, debug channel `GameCode/GameActions` — names it) keeps the *effective* entry, re-applying
only when its `BodyCatMesh` or `Flags` change, and the apply itself is:

```c
SetStatOverride(0x167 MonsterData, entry.BodyCatMesh);
if (!(entry.Flags & 2)) SetStatOverride(0x40 HeadMesh, 0);     // FUN_00748070
if (!(entry.Flags & 1)) SetStatOverride(0x2f HairType,  1);
```

⭐ **So `HeadMesh = 0` is not missing data — it is the value the client writes for itself**, which is why 44 of
the imported `Head Mesh` rows carry exactly that. `Flags` bits 0 and 1 are "keep my own hair / my own head";
bit 2 guards a third effect on the character (`+0x4c`).

⚠️ **Three consequences, and they replace the plan this section used to carry.**

1. **Nothing is sent for a morph.** The client already holds the spell's RDB record — it renders the tooltip
   and the particle effects from it — and applying the buff is the entire trigger. Pushing the eight appearance
   stats over `StatIIR` would be inventing traffic retail does not produce, which is also the honest reading of
   the census above rather than "an extrapolation from one attested member".
2. **The 103 `Scale` records are not morphs.** Two values over a handful of handles, re-asserted dozens of
   times in one capture, is a per-character constant being restated, not a shapeshift landing.
3. **`TempChangeSkill` OVERRIDES the stat, it does not add to it**, and `BuffStatEffects.OverrideKinds` is where
   that lands: the value REPLACES `gear + sum(buffs)` for its stat rather than joining the sum. On a zero-baseline
   stat the two agree, which is why nothing had looked wrong; on a stat gear also feeds they do not.

⭐ **And the client runs these executors on OUR OWN buff packet**, which is what makes the combine rule forced
rather than a free choice (#805). The chain, each link named by its own log string:
`MagicBuffIIR_c::Activate` (`FUN_007a1580` — *"MagicBuffIIR_c::Activate, UpdateBuff with parameters:/n
BuffTemplate "*) → `CoreMagicHolder::UpdateBuff` (`FUN_007ee710`) → `RunGameActions` (`FUN_0089bab0` — *"RunGameActions
Type:%d Duration:%d"*, channel `GameCode/GameAction`) → the per-kind executor's vtable slot 9. Applying a buff is
the whole trigger; the client evaluates the spell's elements from its own RDB copy with nothing else on the wire.
The same loop is reached from the item side (`ItemHolder_c::ActionUnWear` and the feat/gem GameAction maps), which
is how equipping runs an item's elements.

Its ramped twin `53221 InterpolatedTempChangeSkill` is the same override with the value lerped between keys
`0x4b6`/`0x4b7` at the carrier's level (`GameActionMethods_t::TempChangeSkillGameAction`, `FUN_008b1f20` —
its assert string is what names the family).

## One row per ELEMENT, and how a reader resolves the set (#783)

A `SpellStatEffect` row is **one element of the record**, keyed `(SpellId, Sequence)` in record order. It used to be
keyed `(SpellId, StatId, PeriodCentiseconds)`, and that key discarded everything but the last element on a repeated
key — 8,366 rows, a third of the table. Two shapes are behind the repeat, and a reader has to tell them apart:

- **LEVEL BANDS.** A record states its magnitudes once per band. `Hellfire Stream` (8600) carries five
  `InterpolatedHit` elements on `(stat 27, period 0)`, banded `(1-5) (6-10) (11-15) (16-20) (21-30)` — exactly one of
  which is about any given caster.
- **PLAIN REPEATS.** `Life Leech`'s effect record (220980) carries five elements on stat 27 — four heals and a drain —
  all sharing **one** band, so all five apply. `Lifestrike` carries two per rank, a damage element and a heal, which
  is what makes it a life steal; keyed on the stat alone, one of the two was thrown away.

`SpellEffectBands` is the one rule that gets both right: within a `(spell, stat, period)` group keep the banded members
whose band **covers** the level — or, when none does, the nearest band, clamped like the client's own ramp
arithmetic — and sum whatever survives.

⚠️ A record also states the **same element twice**, verbatim: `Hellfire Stream` repeats its level 1-5 and 6-10 bands at
body offsets 1106 and 1194 (the elements do not overlap, so this is in the shipped bytes, not a scan artifact). 1,298
rows over 361 spells are such a repeat. Why is not decoded — the record has 330 bytes nothing accounts for — so the
row is stored verbatim and the collapse happens where it is used: `SpellStatEffect.Statement` is the row minus its
sequence, and both engines de-duplicate on it rather than applying a spell twice because of a structure nobody has read.

## What the server applies

`BuffStatEffects` honors a record only when **both** halves are justified:

1. the kind is one of the flat kinds, and
2. the stat is one whose wire scale we verified against a real in-client tooltip - which is exactly the set the
   server already consumes from gear (Armor, Protection, Hit/Critical/Critical-Damage/Combat Rating, Magic
   Damage, the two Natural Regens) plus the five attributes and the three max pools.

Everything else is imported but inert, and counted in the engine's load summary. Applying a percent record as
flat would turn "+2% Critical Rating" into "+200"; applying a flat record on a stat whose scale is unverified
(`Absorb`, `Retributive Damage`) would be a guess with no way to notice it was wrong.

### Percent records (#436)

The percent kind is honored too, on the same stat gate — it just needs its own arithmetic, because a percentage
scales a total rather than adding to one:

```
value = (gear + flat buffs) × (1 + Σ percents / 10000)
```

- **The denominator is 10000.** Retail keeps a percentage in a stat the same way: a live read of the fatality buff
  shows stat `536` *Magic Damage Modifier* moving `10000 → 12000` for a +20% effect, so `10000` is `1.00`.
- **Percentages ADD rather than compound** — two +2% buffs are +4%, not +4.04% — and are applied **after** the flat
  deltas, on their total. A flat +50 and a +2% on 250 gear is 306, not 305.
- **Truncated**, like every other client-side percentage read (the regen sheet's integer divide, #442). A percentage
  of 0 leaves the value bit-identical either way.

The whole shipped set is 13 records over 6 spells, and only three of the six stats they touch are modelled —
`Hit Rating` (310), `Critical Rating` (312) and `Combat Rating` (875). `PvP Hit Rating` (311), `Evade Rating` (308)
and `Immunity Rating` (490) stay counted-but-inert, exactly as an unverified flat record would.

### The max pools (#450)

Stats 1 / 505 / 507 (`HealthMax` / `StaminaMax` / `ManaMax`) are stored at the ordinary **×100 vitals scale**, which
the rank ladders force: Sustaining Wrath's nine ranks step `108000 … 180000` in 9000s, i.e. **+1080 … +1800 health**
in 90-point steps - read flat it would be +108,000. Focused Mind carries stat 507 across its whole ladder the same
way. Both are class buffs whose descriptions say only "increases maximum health/mana", so the ladder is the evidence,
not the text.

These do not land in a rating field. A pool is `base + gear + buffs`, so `BuffStatEffects` re-applies the whole bonus
through `EntityVitals.SetMaxFromBonus`, which also clamps CURRENT when the max comes back down - a player left above
their own bar would never regen and would read as permanently full. It is also the one honored stat where the value
pushed to the client is not the buffed total: the wire's `HealthMax` is the whole pool, not the bonus.

## What this region is NOT

It is **not** how the buffs that actually occur in retail play express themselves. Across the whole capture
corpus, 187 distinct spells are applied to a player, and **none of them carries a `[2][9]` region at all** -
Stagger, Shield of the Risen, Weakened Protection, Exhausted, Flesh to Worms and Fatality have none, and the only
stat updates retail sends around those applies are vitals (health/mana/stamina current), never stat deltas. So
this region covers potions, elixirs and a slice of the class buffs; the rest of a buff's behaviour is
server-side content we do not have, exactly as with mana cost, recast and damage
(see [stat-engine.md](../subsystems/stat-engine.md)).

Spell **descriptions** (LDB category 50211, keyed by spell id → `Spells.Description`) are the useful
counterweight: they state in words what an effect does, which is how an effect gets identified before it is
implemented and how a decoded magnitude gets sanity-checked.

## A spell can also modify ANOTHER spell (#710)

Two of the kinds in a spell record do **not** target the spell that carries them, and that is why they needed a
table of their own:

| kind | client name | slots | rows | carriers |
| --- | --- | --- | ---: | ---: |
| 53054 | `SpellStatModifier` | `[spell, Stat, Value, Amount]` | 415 | 133 |
| 53062 | `SpellLineStatModifier` | `[line, Stat, Value, Amount]` | 1,156 | 117 |

They read "while this is up, make spell X — or every rank of line L — crit harder / cost less mana / cast
faster". `SpellStatEffects` is keyed by `(SpellId, Sequence)` with the carrier as the **subject**, so it had
nowhere to put the target: the rows were imported with their stat and their magnitude and the **target dropped on
the floor**, which is the one field that makes them mean anything. `SpellSpellStatModifier` is where they live now.

⭐ **One mechanism, two sources.** #593 imported the identical record off **feats** (`FeatSpellStatModifier`) —
same kinds, same key lists, same four-slot payload. A feat that makes your Shock cheaper and a buff that makes
your next Shock cheaper differ only in where the row was read from, so the parser
(`ItemTemplateStatModifiers.ParseSpellStatModifiers`) and the consumer (`SpellModifierService`) are shared.

The targets resolve, which is what says the decode is right rather than merely self-consistent: **415/415** of the
spell-scoped targets are a `Spell.Id` and **1,146/1,156** of the line-scoped ones a `Spell.GuiStackingLine`.

`Value` and `PercentHundredths` are alternatives, never a pair. A percentage needs no unit — `-1000` is `-10%` of
whatever the base is — and that half was applied from the start.

⭐ **The ABSOLUTE half is pinned too (#787), and by two sources that agree on every stat.** A modifier states its
delta in the units of the PROPERTY-RUN FIELD it names — the same units the column was built from — so the factor
is whatever the importer already applies on the way in, and the client's own divisor table
(`StatDisplayScale`, from `FUN_011197f0`/`FUN_01119ca0`) independently agrees:

| stat | column | importer | client ÷ | suffix | shipped `Value` | reads as |
|---|---|---|---:|:---:|---|---|
| `129` Spell Critical Damage | `CriticalDamageHundredths` | raw | 100 | `%` | 1000..10000 | +10% .. +100% |
| `147` Spell Critical Chance | `CriticalChanceHundredths` | raw | 100 | `%` | 50..10000 | +0.5% .. +100% |
| `294` Casting Time | `CastTime` (ms) | `× 10` | 100 | — | -900..100 | -9.00 s .. +1.00 s |
| `8` Duration | `DurationMs` | `× 10` | 100 | — | -100..4500 | -1.00 s .. +45.00 s |
| `348` Splash Damage | `SplashDamage` | `/ 100f` | 100 | `%` | 400..4500 | +4% .. +45% |
| `347` Splash Radius | `SplashRadius` | `/ 100f` | 100 | — | 50..500 | 0.5 .. 5.0 m |
| `349` Splash max targets | `SplashMaxTargets` | raw | **1** | — | 1..20 | 1..20 targets |
| `132` Mana Cost | `ManaCost` | raw | — | — | 2..20 | raw |

⭐ `349` is the check the ticket named: the client scales it by **1** where every neighbour is 100, and every
shipped modifier on it really is a small integer. Two tables that could disagree, and do not.

`SpellModifiers.Apply` composes both halves — **additive first, then relative**, which is the client's own order
for a value carrying both (`FUN_014a9c10` forms `value + additive` and hands it to `FUN_014a9f90`, which
multiplies). The two live consumers (`Mana Cost`, `Casting Time`) use it, so the 99 absolute cast-time rows that
were read and dropped now shorten a cast.

[V3: SpellSpellStatModifierTests.EveryTarget_ResolvesAgainstTheShippedCatalog]
[V3: SpellSpellStatModifierTests.Apply_ConvertsACentisecondModifierIntoTheMillisecondColumn]
