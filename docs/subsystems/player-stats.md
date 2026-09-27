# Player stats — where they live in the packets

Cross-referencing character-sheet screenshots with the capture packets. Reference character: **AssassinL23,
Stygian Assassin, level 23**, decoded against `AssassinL23_rogue-assassin_login_randomActions-changemap.tshark.json`.

## Where the data is

The character stats are inside the big **`0x737A6DF9`** inventory/character-state snapshot (the same packet
family that carries inventory). In AssassinL23's snapshot (79431 bytes) the stat
block sits near the end, roughly offset **78000–78800**.

## Encoding

A run of fixed **8-byte records: `[uint32 statId BE][uint32 value BE]`** — the `(statId << 32) | value`
tagged-uint64 encoding documented on the `CharacterStatId` enum
(`Faolan.Core/Enums/CharacterStatId.cs`) and emitted/parsed by the `CharacterStatBlock` codec
(`Faolan.Core.Network/Codecs/FullCharSnapshot/CharacterStatBlock.cs`). There is no 7-byte "packed" variant: a scan
for one matches every 8-byte record minus its leading zero byte.

## The wire → display scale is the client's rule, and it is fully recovered

Every stat travels in wire units and the client divides only when it draws, so a scale can never be read off the
wire or off the client's own live stat table — both sides are wire. Measuring one stat at a time from a rendered
number (a tooltip, or the character sheet through `.setstat`) cannot reach stats a low-level sheet does not draw.

The modifier line of an item/feat tooltip is rendered by `FUN_0149a300(text, value, invertGoodBad)`, and that
function is the scale rule — it picks a float divisor
and then prints either `value / divisor` (`FUN_0149a5c0`) or, when none applies, the raw integer
(`FUN_0149a540`):

```
divisor = 1.0                                            # 0x01f27500
if (predicate1(stat)) divisor  = 100.0                   # 0x01f24468
if (predicate2(stat)) divisor *= 100.0                   # the two COMPOSE -> 10000
if (stat in {0x324,0x326,0x328,0x32A,0x32C,0x32E,0x57B}) divisor = 10.0    # 0x01f68aa4, overrides
```

The two predicates are signal slots the client registers at startup — `FUN_011196e0` stores them at
`DAT_029c8200` / `DAT_029c8204` with the RTTI type
`SlotFull1<SignalTarget, SignalSignatureStat1<bool, stats::Int32Stat_e, SignalTarget>, …>`, i.e.
`bool(Int32Stat_e)` — and `FUN_00651ca0` is the generic invoker that dispatches through them. Their bodies are
`FUN_011197f0` and `FUN_01119ca0`, each one large jump table of stat ids. Both are transcribed in
`Faolan.Core/Enums/StatDisplayScale.cs`; `CharacterStatId.GetScale()` forwards to it.

It reproduces all 27 values measured by hand (`Faolan.Tests/StatDisplayScaleTests.cs`),
including the pair that warns against inferring a scale from a neighbour — `0x0177 FatalityChance` prints its
raw value with a `%` suffix (a format, not a scale) while `0x017C SpellFatalityChance` five ids away is a real
×100. That agreement is what licenses the transcribed table over a measured one.

Consequences worth knowing:

- **Stats a sheet never draws are answered.** ×100: `920` Attack Rating, `922` Defense Rating,
  `923` Magic Rating, `925` Magical Resist, `252` Miss Chance, `535` Healing Efficiency, `861` Magic Damage
  Modifier. ×1: `974` Damage Deflection, `1037` Knockback Resistance, `156` Chance of Being Staggered.
- **×10000 is real.** Five ids sit in both predicate sets and the client multiplies the divisors (an explicit
  `MULSS` against the same `100.0` constant): `0x00FF` Evade Level Growth, `0x013C` Variance, `0x013D`,
  `0x0140` Magic Variance, `0x0397` Critical Blast Chance.
- **×1 is an answer, not a default.** An id in neither set takes the client's integer formatter.

The one deliberate divergence is `BuffStatEffects.SpellScale`: spells store attribute amounts in display units while
items store them ×10, so it overrides the five attributes to ×1. That is a difference between two data sources, not a
disagreement with the client.

### What `BuffStatEffects` honors, and the test that decides it

Of the 5,369 imported `SpellStatEffect` rows, 2,624 (49%) are applied — 2,179 as lasting stat deltas here and 445 as
instant pool moves by `SpellPoolEffects` (healing is open work tracked in Gitea #702). The rest split into two very different piles, and
conflating them is the mistake to avoid:

| dropped by | rows | what they are |
| --- | ---: | --- |
| kind | 502 over 9 kinds | not stat deltas at all — `Set` (155), `SpellLineStatModifier` (149) and `SpellStatModifier` (123) which target another spell rather than the carrier, `SetFlag` (47), `AddAbsorb` (12) |
| stat | 2,243 over ~140 stats | real deltas on stats not on the allowlist — led by `DamageMultiplier` (316), `NaturalEnergyRegeneration` (198), `WeaponDamageMelee` (184), `Scale` (153), `MagicDamageModifier` (152) |

**Re-derive those counts, never hand-take them.** `dotnet run --project Faolan.Importer -- --report
spell-effect-gap` asks the two engines to classify every shipped row and prints exactly this breakdown (`--report-arg
<kind or stat id>` lists the rows behind one line, with the spell that carries them). A hand census goes stale as the
engines change underneath it.

**Ranking the kinds by row count misleads.** What says whether admitting a kind is worth anything is how
many of its rows land on a stat the engine *already models*, and the two numbers are nearly unrelated: of the 502
rows the kind gate drops, 5 are on a modelled stat. The report prints that as its own column.

**"Is the wire scale verified" excludes nothing**, because `StatDisplayScale` transcribes the client's own divisor
rule and covers every id; reading it as the admission test leaves stats wrongly held back. Knockback Resistance
(`Turtle Formation`'s +100; `KnockbackService.Knock` reads it off `BuffedStats`), Damage Deflection (`Phoenix Cloak`'s
+40/+50) and Chance of Being Staggered (the −20 every horse's `Mounted` buff carries) are honored; the last two are
sheet-only, because nothing server-side reads them — there is no stagger mechanic (open work tracked in Gitea #699).

**The test that does work is the baseline.** `Recompute` computes `gear + sum(buffs)`, so a stat is
admissible only if its baseline is zero. A multiplier stat is not:

- `MovementMultiplier` (`0x0F0`) has a baseline of 100 and `Turtle Formation` carries −50 on it. Summed from
  zero that tells the client "movement 50%" for a buff that means "−50%".
- `MagicDamageModifier` (`0x218`) is worse: baseline 10000, and a live read shows it moving 10000 → 12000 for a
  +20% effect.

So the rule is: add a stat when its records add to zero, whatever their scale; leave it off when they scale a
total. The multiplier family needs a model that knows the baseline (open work tracked in Gitea #700), not a place on
the list.
[V3: BuffStatEffectsTests.AdditiveFamilies_AreHonoredWhileMultiplierStatsStayOut]

The families admitted on that rule: all eleven damage-school invulnerability ratings (~700 rows — `Magic
Shield` +10000 cold is +100%, `Crom` +20000, `Fire Pit` −20000), the three out-of-combat regens plus
the `NaturalHealthRegen`/`NaturalManaRegen`/`NaturalStaminaRegen` run (`0x383`/`0x384`/`0x385`), `EvadeChance`, `BaseSpellDamage` and `HealthTapAmount`.

### The kind half of the gate has its own admission test, and it is the unit

The baseline rule above decides stats. What decides a kind is whether its amount arrives in the units the stat is
proven to use. `53110 TempChangeSkill` and its ramped twin `53221 InterpolatedTempChangeSkill` stand to each other
exactly as the honored 53045 and 53220 do, and all four carry the same `[0 Stat, 39 Value]` slots, so both are
honored.

The units confirm it. `Skin of Iron` carries 10000 on slashing/piercing/crushing/poison invulnerability — the
same +100% step `Magic Shield` states with 53045 on cold — and `Soul of Mitra`, whose client description reads
*"shielding the priest from almost all damage"*, carries 90 damage deflection beside `Phoenix Cloak`'s 53045
+40/+50.

Admitting the kind admits nothing else, because the two halves of the gate are independent: 53110's bulk
is a polymorph tail (Head Mesh, Monster Data, Scale, Super Family) plus Natural Energy Regeneration, and the stat
half holds all of it — a shapeshift is not `gear + sum(buffs)` in any units.
[V3: BuffStatEffectsTests.TempChangeSkill_IsHonored_LikeItsAlreadyHonoredInterpolatedTwin]

### The combine rule is a third question: 53110 / 53221 override the stat

Unit and shape do not say how the value meets what the stat already holds, and for these two the client answers
it: `53110`'s executor is `SetStatOverride(statTable, Stat, Value)` and its remove is `ClearStatOverride`
(`53221` is the same call with the value lerped between the ramp's endpoints — `docs/re/gameaction-executors.tsv`).
So a `Soul of Mitra` on a character already carrying +25 deflection from gear reads 90, not 115.

**That is forced rather than chosen, because the client runs that executor on our own buff packet.**
`MagicBuffIIR_c::Activate` → `CoreMagicHolder::UpdateBuff` → `RunGameActions` → the executor's vtable slot 9: the
packet that applies a buff makes the client evaluate the spell's elements out of its own RDB copy, with nothing
else on the wire. A server summing `gear + amount` would be a second opinion on a number the client had already
set. Two overrides on one stat is the one part that is ours: the client keeps a single override slot per
stat (last write wins, an order we do not have), so the strongest magnitude wins here.
[V3: BuffStatEffectsTests.OverrideKind_ReplacesTheGearContributionRatherThanAddingToIt]
## Confirmed stat ids (decoded value matches the screenshot exactly)

| statId | name | raw | displayed | screenshot |
|--------|------|-----|-----------|------------|
| `0x0036` | Level | 23 | 23 | 23 ✓ |
| `0x001B` | HealthCurrent | 127000 | 1270 | 1270 / 1270 ✓ |
| `0x01FA` | ManaCurrent | 7400 | 74 | 74 / 74 ✓ |
| `0x01F8` | StaminaCurrent | 114800 | 1148 | 1148 / 1148 ✓ |
| `0x0290` | PvPTotalKills | 9 | 9 | Kills overall 9 ✓ |
| `0x0292` | PvPTotalDeaths | 11 | 11 | Deaths overall 11 ✓ |
| `0x0033` | PvpExp | 0 | 0 | PvP exp 0 ✓ |
| `0x0161` | PvpMaxExp | 3500 | 3500 | PvP exp `/ 3500` ✓ |
| `0x0046` | PvpLevel | 0 | 0 | PvP level 0 ✓ |
| `0x0034` | Exp | 242199 | — | cumulative total (sheet shows within-level 11959/33400) |
| `0x015E` | MaxExp | 263640 | — | cumulative total |

`0x0290` / `0x0292` are the client's own compiled-registry names `PvPTotalKills` / `PvPTotalDeaths`, and the
screenshot's wording corroborates them: "overall" is the opposite of *seasonal*, and both ids have a `*Seasonal` twin four slots apart
(`0x566` / `0x567`). It is a career PvP total, not a total of all kills.

Three statements agree, and they are independent of each other:

* the compiled registry's names;
* the contiguity - retail's live level-80 holder carries `0x290 = 239`, `0x292 = 521`, `CaptureSkullWins`
  `0x29C = 13` and `CaptureSkullLosses` `0x29D = 10`, i.e. one unbroken PvP/minigame career block;
* the ratio - 239 kills against 521 deaths is a PvP record. No character levels to 80 dying twice per kill.

`NPCKills` (`0x298`) / `NPCDeaths` (`0x299`) are the only two ids of that `0x290`-`0x29F` run absent from
that holder's 240 stats. The client names them; retail does not populate them. So the PvE counters our server
keeps (`Character.NpcKills`/`NpcDeaths`) stay server-side and go nowhere on the wire.

The screenshot was at full, so the Health/Mana/Stamina current ids above read the same as max. The matching max
ids come from the vitals packet — see next section.

## Vitals are sent as a (current, max) pair — ids proven in-client

The `0x96B8DC59` vitals packet sends a current and a max record for each of Health/Stamina/Mana. The assignment is
proven in-client with live `.setstat` probes (recorded on `CharacterStatId`): setting `0x0001` moves the max health
bar, `0x001B` moves current; `0x01F8` moves stamina current, `0x01FA` mana current:

| vital | current id | max id |
|---|---|---|
| Health | `0x001B` | `0x0001` |
| Stamina | `0x01F8` | `0x01F9` |
| Mana | `0x01FA` | `0x01FB` |

(Stamina/Mana pairs are adjacent ids; Health's current id `0x001B` is not adjacent to its max `0x0001`.) A "the
larger-valued id of each pair is the max" heuristic gets this backwards: a full-health capture has current == max,
and the common announce packet — every entity announces its full vitals on spawn — carries only the current ids
`{0x1B,0x1F8,0x1FA}`. `EntityVitals.ToVitalsBlock` emits both ids per vital; a delta sent under the max id grows the
max bar instead of moving current. See `CharacterStatBlockTests`.

## The max ids carry the class base, not the drawn maximum

The two ids of a pair are not "current" and "the number on the bar". The current id carries the whole pool;
the max id carries the class/level base, which the client tops up with its own attribute, gear and feat terms
to produce what it draws. Retail's world-entry push for AssassinL23 (Assassin L23) carries both in one packet:

| id | | raw | what it is |
|---|---|---|---|
| `0x001B` | HealthCurrent | 127000 | the pool — and what the sheet draws as the MAX, 1270 |
| `0x0001` | HealthMax | 77200 | the class base, 772 |
| `0x01F8` | StaminaCurrent | 114800 | 1148 |
| `0x01F9` | StaminaMax | 73600 | 736 |
| `0x01FA` | ManaCurrent | 7400 | 74 — entirely from a worn item |
| `0x01FB` | ManaMax | 0 | an Assassin has no mana pool, item or not |

Two consequences the server has to respect:

- **The server's ceiling must be the drawn maximum.** Putting our ceiling in a max slot makes the client top up
  an already-topped-up number (it drew 1754 where retail drew 1270); putting the class base in the current slot
  tells the client a current the server does not hold. `CharacterMaxStats.For` computes the ceiling and
  `EntityVitals.ClassBasePools` carries the base — see `docs/rdb/client-data-tables.md` for the curve.
- **A 0 in a max slot is what hides a bar.** A class with no mana pool sends `ManaMax 0` even while wearing a
  +74 mana item; sending the item's 74 there makes a mana bar appear on the next vitals push.

## Regen moves the same stats, through the same client setter (`RegenIIR` 0x28D0AC99)

Retail does not push a full `0x96B8DC59` block per regen tick — it sends `RegenIIR`, a 12-byte
`[+health][+mana][+stamina]` signed delta in the same ×100 scale, which the client adds to the three current
ids above. Both applies end in the same virtual call, so a client that renders one renders the other:

| IIR | apply | what it does |
|---|---|---|
| `StatUpdateIIR` `0x96B8DC59` | `FUN_007a00e0` | per record: `(entity+0x10c)->vt[0xc](statId, value)` |
| `RegenIIR` `0x28D0AC99` | `FUN_0079dd10` | per pool: `v = vt[8](statId)`, then `vt[0xc](statId, v + delta)` for `0x1B`, `0x1FA`, `0x1F8` in field order |

Both resolve the target from the carrier's `[0xC350][handle]` and bail to a log channel (`GameCode/Regen`,
`GameCode/Stats`) when the handle names no character. All 1502 captured `RegenIIR`s are 29-byte bodies with
`env == 0`, so there is no conditional field [V3: V1IirCodecTests.Regen_RoundTripsCapturedBody].

Retail pins the endpoint with an authoritative block on the tick a pool tops off — in
`Hoxxling_mage-heraldofxotli_login_gameplay` at 18:49:49.912 a `RegenIIR(2060, 1992, 882)` ships in the same frame
as a stat block setting stamina `0x1F8` = 14000 and mana `0x1FA` = 28800 (their maxes). `WorldSimulation`'s regen
tick sends the delta and then the block on every tick (delta first, so the absolute value lands last and the client
can never double-apply): the delta alone does not visibly regen in-client, so the block is what moves the bars, and
trimming it to retail's top-off-only shape needs the delta channel proven in-client first.

## The regen rates — attribute-driven, and confirmed against the captured deltas

A `RegenIIR` delta is the per-second rate ×100, so the corpus states retail's rate for a known character to two
decimals. That is what pins these formulas; they use the coefficients out of the client's own tweak table (RDB
1000516, [`docs/rdb/client-tweak-table.md`](../rdb/client-tweak-table.md)), in display units, per second:

| pool | rate | attributes |
|---|---|---|
| health | `0.15 × (Con + Str) + 20` | Constitution, Strength |
| stamina | `0.15 × (Con + Str + Dex) + 10` | + Dexterity |
| mana | `0.38 × (Int + Wis) + class base` | Intelligence, Wisdom (the base is class data, not a constant) |

Each against the captured delta for that character, exactly [V1: Faolan.Tests/RegenRatesTests.cs]:

| character | scores | rate | captured `RegenIIR` |
|---|---|---|---|
| NecroL80, L80 Necromancer | Con 375, Str 70 | 86.75 | hp `8675` |
| DemoL80, L80 Demonologist | Con 345, Str 70 | 82.25 | hp `20353` = `8225` + 12128 |
| AssassinL23, L23 Assassin | Con 56, Str 20, Dex 79, Int 39, Wis 26 | 31.40 / 33.25 / 24.70 | hp `3140`, stam `3325`, mana `2470` |
| Hakotep, L24 Herald of Xotli | Int 118, Wis 33, class mana base 20.00 | 77.38 | mana `7738` |

**The client computes the sheet's row the same way**, from its own copy of the same tweaks — `FUN_0111a520` writes stat
`0x116` (health) and `FUN_0111a6e0` writes `0x117` (stamina), reading `0x02D244A0`/`B0` and `E0`/`F0`/`500`, all 0.15 in
the running process. Its form is

```
stat(0x116) += Σ modifier × (attribute − 10) × 0.1 × 100        // attribute at the ×10 wire scale
```

i.e. the pool's regen stat's own value plus an attribute trickledown on top (hence the sheet's
`HealthOOCRegenTrickledown` tooltip row). That is the table above, not merely close to it, once the attribute scale
is right: the wire value is `display × 10 + 10`, because the client seeds every attribute to 10 before applying
anything, so its `(raw − 10) × 0.1` is just a conversion back to display units. The `ClientInterface` channel shows
both halves directly at world entry — `(804 10 old:0)` then `(804 710 old:10)` for a display Strength of 70, and stats
`278`/`279`/`280` at 2000 / 1000 / 2000 = display 20.00 / 10.00 / 20.00 (for a caster; see below for 280).

So the flat terms are those seeded stat values, and the server's formula and the client's displayed row are the same
computation. It also explains the `(attribute − 10) / 10` term in the armour curve (`CombatResolver`): that is the same
raw→display conversion, which is why the trace's AttributeBonus of 140 is `(710 − 10) / 10 × 2.0` for a display 70.

**Mana's base is class data.** Stat 280 (Non Combat Mana Regen) is never written at all for the level 23 Assassin
(base 0), is 2000 for the level 24 Herald of Xotli, and 2200 for both level 80 mage classes. Two client sources add:
the auto-trained `Mage` / `Priest` feat's 2000 and the class template's stat 280 (200 on the level-80 casters, none on
the Herald) — `WorldEntity.NonCombatManaRegenBase`. Two captured deltas confirm the branch
with two *different* bases, both exactly:

| character | Int + Wis | class base | rate | captured |
|---|---|---|---|---|
| AssassinL23, L23 Assassin | 39 + 26 = 65 | 0 | 24.70 | `2470` |
| Hakotep, L24 Herald of Xotli | 118 + 33 = 151 | 20.00 | 77.38 | `7738` |

So the 0.38 modifier and the base-plus-trickledown shape are pinned for mana the same way they are for health and
stamina. `RegenRates.ManaPerSecond` takes the base as an argument.

**The level 80 casters' mana deltas belong to a different channel — do not fit this formula to them.** They are not
full-pool restores either (NecroL80's delta `6523` is a ×100 wire value, 65.23/s, while her 6523 max mana is a
display value — same digits, different quantities). Lined up properly,
their most common deltas are exactly 1% and 2% of max mana per second — NecroL80 `6523` and `13046` on a 6523 pool,
DemoL80 `5830` on 5830 — and none of their deltas inverts to a whole attribute sum under any observed class base.
Plausibly the `TWK_NaturalManaRegen*` in-combat twin; undecoded either way.

The health and stamina bases do NOT vary this way in the three characters observed — 2000 and 1000 for all of them,
including a Necromancer that has no stamina pool at all.

The trickledown is not written back into the stat: 278/279 stay at their seeded values in the stat
store for the whole session (one write each), which matches the disassembly — the routine returns `base + trickledown`
to its caller rather than storing it, so the sheet computes the row at display time.

Three more things worth keeping:

- **The cadence is 1 Hz.** Median inter-arrival across all 1502 captured `RegenIIR`s is 1.00 s (min 0.77); the longer
  gaps are pools already full, not a slower tick. So the sheet's "Non Combat Health Regen" row IS the per-second rate.
- **The flat terms come from truncation, and reading the sheet as rounded gets them wrong.** The sheet prints the stat
  with an integer divide by 100 (client `FUN_016d9cb0`, case `0x116` = stat 278 `Non Combat Health Regen`), so 86.75
  and 82.25 both display as their floor. Solving the three displayed integers as *rounded* values yields 19.5 and
  fits all of them; solving them as *truncated* yields 20 — and only 20 reproduces the captured deltas.
- **DemoL80's bare rate was never captured**, only `20353`. It is identified as `8225` because NecroL80's boosted
  `20803` exceeds her bare `8675` by exactly the same 12128 — one shared additive term (a buff or rest bonus, not
  decoded), not a per-character rate.

### The class attribute base

The scores the formulas need are computed client-side (retail sends a flat 10 for every attribute), and the base is
not in any of the 25 named `CoreTables` data tables — `RDBPLAYERCLASSDATA` reads RDB types 1000505–1000508, which
are name/id maps, and nothing in 1000509–1000521 has a 12-class × 80-level shape.

It is in **RDB type 1000020**, the item-template type, as 80 templates per class — one per level. They are
plaintext — no TEA key, unlike ordinary item templates — and their stat records are the base attributes, read by the
same byte-aligned `ItemTemplateStatModifiers.Parse` as any item. The Assassin's:

| level | id | Str | Con | Int | Wis | Dex |
|---|---|---|---|---|---|---|
| 1 | 3074028 | 10 | 10 | 10 | 10 | 10 |
| 2 | 3074029 | 20 | 20 | 20 | 20 | 40 |
| 23 | 3074050 | 200 | 170 | 260 | 200 | **650** |
| 80 | 3074108 | 700 | 600 | 900 | 700 | **2300** |

Level 1 is the neutral 10 across the board (display 0), the curve is monotonic, and Dexterity leads throughout — the
right shape for the class. The level-23 row is exactly what the client applied for our level-23 Assassin.

The same templates also carry `MaxHealth` and, for the level-80 caster classes, stat 280 = 200 — the +200 that takes
the mana-regen base from the feat's 2000 to the 2200 those classes show.

#### The level comes from the template, not from its id

Each template names itself `"<Class> Class Feat <NNN>"`, and `NNN` is the first level of a ten-level block:
001, 011, 021 … 071. Inside a block the ten members are ordered by their values — the five attributes rise strictly
with level — so:

```
level = NNN + rank-inside-the-block-by-attribute-sum
```

**`id = base + (level − 1)` is wrong.** A class's 80 ids are consecutive, but the eight blocks sit inside that range
in an arbitrary, per-class order. The Ranger's run is `[31-40][41-50][11-20][71-80][61-70][51-60][21-30][1-10]` (the
level-1 block is last), and the id rule would import the Priest of Mitra's level 61–70 values as levels 11–20.

Two adjacent traps the block rule also clears up:

- **The `<NNN>` is the level decade, not the level.** "Three consecutive Assassin templates are all `Class Feat 021`"
  is not noise — they are levels 21, 22 and 23.
- **Some blocks carry strays** from another feat series that shares the class name (5 in the Ranger's block 001, 10 in
  the Herald of Xotli's block 021). They are told apart by the stat ids they write: a class-base template only ever
  writes the five even attribute ids, `MaxHealth` and stat 280, never the odd-id twins `805/807/809/811/813/815`
  (which the client seeds to 100 — a percentage, not a score). Their attribute amounts are single digits rather than
  the base's multiples of ten.

`Tempest of Set` is stored in the RDB under the class's old name, Stormcaller.

#### Coverage and verification

All 12 classes × all 80 levels resolve, out of exactly 80 candidates each. Nothing about the layout is assumed: the
scan is driven by the name and the values, and the client observations are the *acceptance test*, all thirteen of which
it reproduces —

| class | observed | | class | observed |
|---|---|---|---|---|
| Barbarian | L1 → 1083955 ✓ | | Dark Templar | L1 → 793759 ✓ |
| Guardian | L1 → 871090 ✓ | | Assassin | L1 → 3074028 ✓, L23 → 3074050 ✓ |
| Conqueror | L1 → 873357 ✓ | | Ranger | L1 → 3115334 ✓ |
| Priest of Mitra | L1 → 3192040 ✓ | | Necromancer | L80 → 1173863 ✓ |
| Tempest of Set | L1 → 3067820 ✓ | | Herald of Xotli | L24 → 3130655 ✓ |
| Bear Shaman | L1 → 1102716 ✓ | | Demonologist | L80 → 1126759 ✓ |

Level-1 ids back-computed as `observed − (level − 1)` under the id rule are wrong (for the Herald of Xotli,
Necromancer and Demonologist the arithmetic absorbs unreadable ids in between or crosses into another block
entirely). Never re-derive an observation; re-observe it — a level-1 character of the class brought in-game with the `GameCode/ItemData`
+ `ClientInterface` channels on, where the template loaded before the five attribute writes IS the base.

Shipped as the `class-attributes` importer module into `ClassAttributeBases`, and `WorldEntity.AttributeScore` returns
`classBase + gear + buffs`. A `(class, level)` with no row is treated as unknown, not zero: world entry logs a
warning naming the character, and that character's attributes are gear + buffs only.

## Attributes are client-derived

The attribute stats `Strength 0x0324 / Constitution 0x0326 / Intelligence 0x0328 / Wisdom 0x032A / Dexterity 0x032E`
do not carry the sheet's scores: the only clean value the wire carries for those ids is a base `10` (= 1.0 at ×10),
and the sheet values (Str 20, Int 39, Con 56, Dex 79, Wis 26 for AssassinL23, and ×10) appear nowhere as the value
following their stat id. The client computes the displayed attributes (class base + gear + buffs) — the class base
section above. (The on-wire stat set is bounded by `g_Int32StatsOrder`, asserted `== generic_stats_size()` in
`CoreCharacterFullUpdateIIR::ReadSubClass`; the array is built indirectly via vector growth, so enumerating its full
id list needs the registration-call route, not a direct-write xref.)

## Stat-block framing (resolved via the combat vitals packet)

The stat records are **count-prefixed**: a 1-byte record count, then N × 8-byte `[statId][value]` records. This
is clearest in the `0x96B8DC59` entity-vitals packet (`...<entityHandle><00000000><count:1><records>`, see
`docs/re/quests-and-combat.md`); the same record encoding is embedded in the larger `0x737A6DF9` snapshot.

## Who authors the login snapshot's sheet

`SnapshotStatSheet` — one place, for every character. It picks the baseline from provenance (an imported
character's real captured sheet, else `SubClassStatBlock.FreshSheet()`) and then applies the same override
table either way, by stat id:

| stat | source |
|---|---|
| `0xBF` PlayfieldToken | the live zone's minted token — session state, never the captured one |
| `0x0D7` GmLevel | the account type's GM privilege mask ([gm-commands.md](gm-commands.md)) |
| `0x36` Level | the character (a captured sheet's level is stale the moment it dings) |
| `0x34` Exp / `0x15E` MaxExp / `0x39` ExpCurrentLevelBase | the XP triple, authored together by `CharacterStatBlock.SetExperience` — see [The XP triple is cumulative](#the-xp-triple-is-cumulative) |

One authoring path matters: two paths patching different slots let an override added to one silently do nothing
on the other.

**Word 80 of the stat block is `MaxExp`, not experience.** A level-1 beach character's MaxExp is 100, so
`Experience = 10` × a "factor 10" produces the right bytes for that one case by coincidence; the golden passes with
the character's real, zero, experience and MaxExp authored from the curve.

### The XP triple is cumulative

The three XP stats are a running total and its two boundaries, not a within-level amount:

| stat | meaning |
|---|---|
| `0x034` `Exp` | the character's running total |
| `0x039` `ExpCurrentLevelBase` | the running total at which the current level began |
| `0x15E` `MaxExp` | the running total at which the next level begins |

so the client fills the bar as `(Exp - Base) / (MaxExp - Base)` and a level's cost is `MaxExp - Base`. Three
independent readings of the corpus say so: every captured snapshot has `Base <= Exp <= MaxExp`; a level-23
character carries `base=230240 max=263640` while a level-24 one carries `base=263640` — *the same number*; and the
`NewLevelIIR` ding's six "deltas" are `[Exp, Base, MaxExp, 1, 8, 0]`, where only the first varies between players
dinging to the same level. [V3: LevelingCurveCorpusTests.TheResolverReproducesEveryCapturedXpTriple]

`LevelingResolver` therefore stores thresholds, not costs, and derives `XpForNextLevel` as a difference (reading
`MaxExp` as a per-level cost makes level 24 ask for 298,040 XP instead of its real 34,400).

The curve is server data: the client ships none of it. There is no XP table among the 25 named `CoreTables`
data tables, `GAMETWEAKS` (RDB 1000516) has no level-threshold entry, and the exe's only XP strings are the three
UI value names it fills from these very stats. So the anchors are the levels a capture pinned — 1–6, 8, 9, 23–25,
80 (and 81, which is only ever a bar boundary) — and everything between them is linearly interpolated on the
cumulative curve. The character row keeps within-level progress; the base is added back when the triple is
authored.

The per-class vitals are still positional (`ApplyClassVitals`, slots 21/23/54/56/60/62/64/66/68 with their +148
copies) because several of them share a stat id within one list and the id alone cannot say which is which. That
`CharacterValueIndex` map and the `(statId, value)` sheet are two models of the same words, and where they have
been checked against each other they disagree.

### `HeroicMode` (0x0A9) — a stat we author without knowing what a value selects

It varies in retail: 183 of the 184 captured snapshots carry `0` and one carries `1`, so a fixed value is a character
wearing someone else's number. `Character.HeroicMode` persists it and `SnapshotStatSheet` authors it, with
`.heroicmode <n>` as the setter. That — a per-character value not being shipped as a constant — is the entire
justification. It is not a claim about what the number means.

**The client names it both ways** — the exe's descriptor registry
`FUN_01db2740` says `HeroicMode` (it pairs the string with `MOV [0x029959c0], 0xa9` on the next instruction) while
LDB category 615 says `ChosenGod`. We take the exe's: it is compiled with this build, it names the whole
`1000000+` range the LDB never reaches, and `player_stats.heroic_mode` is a real field of the client's own
protobuf schema. A display string is what goes stale when a stat slot is repurposed.

**The value is carried as an opaque wire number**, and the LDB name has nothing to be about — there is no religion
system in this game to model:

- **Age of Conan has no player-choosable religion.** Deities are lore plus the three *priest classes'* patron gods
  (Priest of Mitra, Tempest of Set, Bear Shaman/Ymir). The AoC wiki has a "Choosing your Class" page and nothing
  for choosing a god. The "pick a religion, find a priest, become an acolyte" system a web search surfaces is
  Conan Exiles — a different game.
- **It is not the priest patron either:** the one captured character carrying `1` is a necromancer.
- **And the client cannot be asked.** It *stores* this stat and never reads it back: `ConstScan` finds exactly two
  uses of `0xA9` as a stat id in the whole image and both are WRITES, there is no `GetStat(0xA9)` anywhere, and the
  one class that gives the id a dedicated slot (`FUN_00843d10` / `FUN_00843d40`, field `+0x244`) only mirrors it —
  see [`../re/aoc-client-stat-names.md`](../re/aoc-client-stat-names.md). It is a server-side number, so
  nothing the client draws depends on the value and no in-client check could confirm a meaning either.

Even calling `0` "not heroic" reads the stat's *name* rather than any evidence — the client never reads the
value back, so nothing it draws could confirm one reading over another.
