# Player stats — where they live in the packets

Cross-referencing the character-sheet **screenshots** (captured under `Captures/screenshots/demol80/`, since
removed from the repo in the tshark-JSON migration) with the capture packets. Reference character:
**AssassinL23, Stygian Assassin, level 23** (screenshot 2026-06-07 17:17), decoded against
`AssassinL23_rogue-assassin_login_randomActions-changemap.tshark.json`. Generated 2026-06-08.

## Where the data is

The character stats are inside the big **`0x737A6DF9`** inventory/character-state snapshot (the same packet
family that carries inventory; see [[inventory_db_items_work]]). In AssassinL23's snapshot (79431 bytes) the stat
block sits near the end, roughly offset **78000–78800**.

## Encoding

A run of fixed **8-byte records: `[uint32 statId BE][uint32 value BE]`** — the `(statId << 32) | value`
tagged-uint64 encoding now documented on the `CharacterStatId` enum
(`Faolan.Core/Enums/CharacterStatId.cs`) and emitted/parsed by the `CharacterStatBlock` codec
(`Faolan.Core.Network/Codecs/FullCharSnapshot/CharacterStatBlock.cs`). (An earlier decode pass also floated a
`PackedStatId24AndUInt32Value` / "packed-7" 7-byte variant; it was a false positive — just a t64 record minus
its leading zero byte, so every t64 hit produced a spurious p7 hit. The shipped codec uses this **single
8-byte encoding**.)

## The wire → display scale is the CLIENT'S rule, and it is fully recovered

Every stat travels in WIRE units and the client divides only when it draws, so a scale can never be read off
the wire or off the client's own live stat table — both sides are wire. It used to be established one stat at
a time from a rendered number (a tooltip, or the character sheet through `.setstat`), which left ten stats
unreachable because a level-1 character's sheet does not draw them.

That is no longer how the table is built. The modifier line of an item/feat tooltip is rendered by
`FUN_0149a300(text, value, invertGoodBad)`, and **that function IS the scale rule** — it picks a float divisor
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

**It reproduces all 27 values that had been measured by hand** (`Faolan.Tests/StatDisplayScaleTests.cs`),
including the pair that warns against inferring a scale from a neighbour — `0x0177 FatalityChance` prints its
raw value with a `%` suffix (a FORMAT, not a scale) while `0x017C SpellFatalityChance` five ids away is a real
×100. That agreement is what licenses replacing the measured table with the transcribed one.

Consequences worth knowing:

- **The ten previously unmeasurable stats are answered.** ×100: `920` Attack Rating, `922` Defense Rating,
  `923` Magic Rating, `925` Magical Resist, `252` Miss Chance, `535` Healing Efficiency, `861` Magic Damage
  Modifier. ×1: `974` Damage Deflection, `1037` Knockback Resistance, `156` Chance of Being Staggered.

  ⚠️ Those last three sat on `BuffStatEffects`' un-honored list for months *after* their scale was settled here,
  which is the failure mode the allowlist invites: it exists to hold back stats whose SCALE is unverified, and
  nothing walks it back when a scale is answered. 370 imported rows were being dropped — `Turtle Formation`'s
  +100 Knockback Resistance, `Phoenix Cloak`'s +40/+50 Damage Deflection, and the −20 Chance of Being Staggered
  every horse's `Mounted` buff carries, which the client draws in its own buff tooltip while the number went
  nowhere. All three are honored now. Knockback Resistance is the one with a live consumer
  (`KnockbackService` reads it off `BuffedStats`), so a resisting BUFF now resists where only a resisting ITEM
  did before; the other two are sheet-only, because nothing server-side reads them — there is no stagger
  mechanic at all (Gitea #699).
- **×10000 is real.** Five ids sit in BOTH predicate sets and the client multiplies the divisors (an explicit
  `MULSS` against the same `100.0` constant): `0x00FF` Evade Level Growth, `0x013C` Variance, `0x013D`,
  `0x0140` Magic Variance, `0x0397` Critical Blast Chance.
- **×1 is now an answer, not a default.** An id in neither set takes the client's integer formatter. The old
  table's standing warning — "assume an id absent from this switch is UNMEASURED rather than ×1", written after
  a ×100 stat sat in the enum but not the table — no longer applies.

⚠️ The one deliberate divergence is `BuffStatEffects.SpellScale`: spells store ATTRIBUTE amounts in display
units while items store them ×10, so it overrides the five attributes to ×1. That is a difference between two
data sources, not a disagreement with the client.

## Confirmed stat ids (decoded value matches the screenshot exactly)

| statId | name | raw | displayed | screenshot |
|--------|------|-----|-----------|------------|
| `0x0036` | Level | 23 | 23 | 23 ✓ |
| `0x001B` | HealthCurrent | 127000 | 1270 | 1270 / 1270 ✓ |
| `0x01FA` | ManaCurrent | 7400 | 74 | 74 / 74 ✓ |
| `0x01F8` | StaminaCurrent | 114800 | 1148 | 1148 / 1148 ✓ |
| `0x0290` | KillingBlows | 9 | 9 | Kills overall 9 ✓ |
| `0x0292` | Death | 11 | 11 | Deaths overall 11 ✓ |
| `0x0033` | PvpExp | 0 | 0 | PvP exp 0 ✓ |
| `0x0161` | PvpMaxExp | 3500 | 3500 | PvP exp `/ 3500` ✓ |
| `0x0046` | PvpLevel | 0 | 0 | PvP level 0 ✓ |
| `0x0034` | Exp | 242199 | — | cumulative total (sheet shows within-level 11959/33400) |
| `0x015E` | MaxExp | 263640 | — | cumulative total |

The screenshot was at full, so the Health/Mana/Stamina **current** ids above read the same as max. The matching
**max** ids were recovered separately from the vitals packet — see next section.

## Vitals are sent as a (current, max) PAIR — current/max ids proven in-client (2026-06-14)

The `0x96B8DC59` vitals packet sends a **current AND a max** record for each of Health/Stamina/Mana. The
current/max id assignment is **proven in-client** (2026-06-14, live `.setstat` probes on Darktemplarchar,
recorded on `CharacterStatId`): setting `0x0001` moved the player's MAX health bar, `0x001B` moved CURRENT;
`0x01F8` moved stamina CURRENT, `0x01FA` moved mana CURRENT:

| vital | **current** id | **max** id |
|---|---|---|
| Health | `0x001B` | `0x0001` |
| Stamina | `0x01F8` | `0x01F9` |
| Mana | `0x01FA` | `0x01FB` |

(Stamina/Mana pairs are adjacent ids; Health's current id `0x001B` is not adjacent to its max `0x0001`.) An
earlier 2026-06-09 pass had guessed the *opposite* mapping from a "the larger-valued id of each pair is the max"
heuristic across the captures; that heuristic was **backwards** — a full-health capture has current == max, so
the common announce packet that uses `{0x1B,0x1F8,0x1FA}` was carrying the CURRENT ids all along. That
**current-only** update (`{0x1B,0x1F8,0x1FA}`) — every entity announces its full vitals on spawn — is why the
screenshot anchor (taken at full) and the spawn-capture test both match those ids. This is now encoded in
`CharacterStatId` (`HealthCurrent`/`HealthMax`/…) and `EntityVitals.ToVitalsBlock` emits both ids per vital (the
old code sent the delta under the *max* id, so a heal/damage grew the player's MAX bar instead of moving
current). See `CharacterStatBlockTests`.

## The max ids carry the CLASS BASE, not the drawn maximum

The two ids of a pair are not "current" and "the number on the bar". The **current** id carries the whole pool;
the **max** id carries the class/level BASE, which the client tops up with its own attribute, gear and feat terms
to produce what it draws. Retail's world-entry push for AssassinL23 (Assassin L23) carries both in one packet:

| id | | raw | what it is |
|---|---|---|---|
| `0x001B` | HealthCurrent | 127000 | the pool — and what the sheet draws as the MAX, 1270 |
| `0x0001` | HealthMax | 77200 | the class base, 772 |
| `0x01F8` | StaminaCurrent | 114800 | 1148 |
| `0x01F9` | StaminaMax | 73600 | 736 |
| `0x01FA` | ManaCurrent | 7400 | 74 — entirely from a worn item |
| `0x01FB` | ManaMax | **0** | an Assassin has no mana pool, item or not |

Two consequences the server has to respect:

- **The server's ceiling must BE the drawn maximum.** Putting our ceiling in a max slot makes the client top up
  an already-topped-up number (it drew 1754 where retail drew 1270); putting the class base in the current slot
  tells the client a current the server does not hold. `CharacterMaxStats.For` computes the ceiling and
  `EntityVitals.ClassBasePools` carries the base — see `docs/rdb/client-data-tables.md` for the curve.
- **A 0 in a max slot is what hides a bar.** A class with no mana pool sends `ManaMax 0` even while wearing a
  +74 mana item; sending the item's 74 there makes a mana bar appear on the next vitals push.

## Regen moves the SAME stats, through the same client setter (`RegenIIR` 0x28D0AC99)

Retail does not push a full `0x96B8DC59` block per regen tick — it sends `RegenIIR`, a 12-byte
`[+health][+mana][+stamina]` signed delta in the same ×100 scale, which the client ADDS to the three **current**
ids above. Both applies end in the same virtual call, so a client that renders one renders the other:

| IIR | apply | what it does |
|---|---|---|
| `StatUpdateIIR` `0x96B8DC59` | `FUN_007a00e0` | per record: `(entity+0x10c)->vt[0xc](statId, value)` |
| `RegenIIR` `0x28D0AC99` | `FUN_0079dd10` | per pool: `v = vt[8](statId)`, then `vt[0xc](statId, v + delta)` for `0x1B`, `0x1FA`, `0x1F8` in field order |

Both resolve the target from the carrier's `[0xC350][handle]` and bail to a log channel (`GameCode/Regen`,
`GameCode/Stats`) when the handle names no character. All 1502 captured `RegenIIR`s are 29-byte bodies with
`env == 0`, so there is no conditional field [V3: V1IirCodecTests.Regen_RoundTripsCapturedBody].

Retail pins the endpoint with an authoritative block on the tick a pool **tops off** — in
`Hoxxling_mage-heraldofxotli_login_gameplay` at 18:49:49.912 a `RegenIIR(2060, 1992, 882)` ships in the same frame
as a stat block setting stamina `0x1F8` = 14000 and mana `0x1FA` = 28800 (their maxes). `WorldSimulation`'s regen
tick sends the delta and then the block on **every** tick (delta first, so the absolute value lands last and the
client can never double-apply); trimming that to retail's top-off-only shape needs the delta channel confirmed
in-client — Gitea #2.

## The regen RATES — attribute-driven, and confirmed against the captured deltas

A `RegenIIR` delta is the per-second rate ×100, so the corpus states retail's rate for a known character to two
decimals. That is what pins these formulas; they use the coefficients out of the client's own tweak table (RDB
1000516, [`docs/rdb/client-tweak-table.md`](../rdb/client-tweak-table.md)), in DISPLAY units, per second:

| pool | rate | attributes |
|---|---|---|
| health | `0.15 × (Con + Str) + 20` | Constitution, Strength |
| stamina | `0.15 × (Con + Str + Dex) + 10` | + Dexterity |
| mana | `0.38 × (Int + Wis) + class base` | Intelligence, Wisdom (the base is CLASS data, not a constant) |

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

i.e. the pool's regen stat's own value plus an attribute **trickledown** on top (hence the sheet's
`HealthOOCRegenTrickledown` tooltip row). That **is** the table above, not merely close to it, once the attribute scale
is right: the wire value is `display × 10 + 10`, because the client seeds every attribute to 10 before applying
anything, so its `(raw − 10) × 0.1` is just a conversion back to display units. The `ClientInterface` channel shows
both halves directly at world entry — `(804 10 old:0)` then `(804 710 old:10)` for a display Strength of 70, and stats
`278`/`279`/`280` seeded to **2000** / **1000** / **2000** = display 20.00 / 10.00 / 20.00.

So the flat terms are those seeded stat values, and the server's formula and the client's displayed row are the same
computation. It also explains the `(attribute − 10) / 10` term in the armour curve (`CombatResolver`): that is the same
raw→display conversion, which is why the trace's AttributeBonus of 140 is `(710 − 10) / 10 × 2.0` for a display 70.

**Mana's base is CLASS data, and that is now confirmed rather than assumed.** Stat 280 (Non Combat Mana Regen) is
**never written at all** for the level 23 Assassin (base 0), is **2000** for the level 24 Herald of Xotli, and **2200**
for both level 80 mage classes (a 2000 seed plus 200 from their class template). Two captured deltas confirm the branch
with two *different* bases, both exactly:

| character | Int + Wis | class base | rate | captured |
|---|---|---|---|---|
| AssassinL23, L23 Assassin | 39 + 26 = 65 | 0 | 24.70 | `2470` |
| Hakotep, L24 Herald of Xotli | 118 + 33 = 151 | 20.00 | 77.38 | `7738` |

So the 0.38 modifier and the base-plus-trickledown shape are pinned for mana the same way they are for health and
stamina. `RegenRates.ManaPerSecond` takes the base as an argument; it is 0 in practice today only because the server
cannot source a class's stat 280 yet (same blocker as the attributes — #444).

⚠️ **The level 80 casters' mana deltas belong to a DIFFERENT channel — do not fit this formula to them.** They are not
full-pool restores either (an earlier reading of ours, from a scale confusion: NecroL80's delta `6523` is a ×100 wire
value, 65.23/s, while her 6523 max mana is a DISPLAY value — same digits, different quantities). Lined up properly,
their most common deltas are exactly **1% and 2% of max mana per second** — NecroL80 `6523` and `13046` on a 6523 pool,
DemoL80 `5830` on 5830 — and *none* of their deltas inverts to a whole attribute sum under any observed class base.
Plausibly the `TWK_NaturalManaRegen*` in-combat twin; undecoded either way.

The health and stamina bases do NOT vary this way in the three characters observed — 2000 and 1000 for all of them,
including a Necromancer that has no stamina pool at all.

Note also that the trickledown is **not written back into the stat**: 278/279 stay at their seeded values in the stat
store for the whole session (one write each), which matches the disassembly — the routine returns `base + trickledown`
to its caller rather than storing it, so the sheet computes the row at display time.

Three more things worth keeping:

- **The cadence is 1 Hz.** Median inter-arrival across all 1502 captured `RegenIIR`s is 1.00 s (min 0.77); the longer
  gaps are pools already full, not a slower tick. So the sheet's "Non Combat Health Regen" row IS the per-second rate.
- **The flat terms come from TRUNCATION, and reading the sheet as rounded gets them wrong.** The sheet prints the stat
  with an integer divide by 100 (client `FUN_016d9cb0`, case `0x116` = stat 278 `Non Combat Health Regen`), so 86.75
  and 82.25 both display as their floor. Solving the three displayed integers as *rounded* values yields 19.5 and
  fits all of them; solving them as *truncated* yields 20 — and only 20 reproduces the captured deltas.
- **DemoL80's bare rate was never captured**, only `20353`. It is identified as `8225` because NecroL80's boosted
  `20803` exceeds her bare `8675` by exactly the same 12128 — one shared additive term (a buff or rest bonus, not
  decoded), not a per-character rate.

### The class attribute BASE — decoded (#444)

The scores the formulas need are computed client-side (retail sends a flat 10 for every attribute), and the base is
**not** in any of the 25 named `CoreTables` data tables — `RDBPLAYERCLASSDATA` reads RDB types 1000505–1000508, which
are name/id maps, and nothing in 1000509–1000521 has a 12-class × 80-level shape.

It is in **RDB type 1000020**, the item-template type, as **80 templates per class — one per LEVEL**. They are
**plaintext** — no TEA key, unlike ordinary item templates — and their stat records ARE the base attributes, read by the
same `ItemTemplateStatModifiers.Parse` as any item (which is why this only became readable after the byte-alignment fix,
#441). The Assassin's:

| level | id | Str | Con | Int | Wis | Dex |
|---|---|---|---|---|---|---|
| 1 | 3074028 | 10 | 10 | 10 | 10 | 10 |
| 2 | 3074029 | 20 | 20 | 20 | 20 | 40 |
| 23 | 3074050 | 200 | 170 | 260 | 200 | **650** |
| 80 | 3074108 | 700 | 600 | 900 | 700 | **2300** |

Level 1 is the neutral 10 across the board (display 0), the curve is monotonic, and Dexterity leads throughout — the
right shape for the class. The level-23 row is exactly what the client applied for our level-23 Assassin.

The same templates also carry **MaxHealth** and, for the caster classes, stat **280 = 200** — which is precisely the
+200 that takes the mana-regen base from its 2000 seed to the 2200 those classes show, closing the last open question
in the regen rates above.

#### The level comes from the template, not from its id

Each template **names itself** `"<Class> Class Feat <NNN>"`, and `NNN` is the first level of a **ten-level block**:
001, 011, 021 … 071. Inside a block the ten members are ordered by their values — the five attributes rise strictly
with level — so:

```
level = NNN + rank-inside-the-block-by-attribute-sum
```

⚠️ **`id = base + (level − 1)` is wrong**, and was the first cut of this decode. A class's 80 ids are consecutive, but
the eight blocks sit inside that range in an **arbitrary, per-class order**. The Ranger's run is
`[31-40][41-50][11-20][71-80][61-70][51-60][21-30][1-10]` — which is why its levels looked like they "stopped dead at
10" (the level-1 block is *last*), and why the Priest of Mitra was importing level 61–70's values as levels 11–20.

Two adjacent traps the block rule also clears up:

- **The `<NNN>` is the level DECADE, not the level.** "Three consecutive Assassin templates are all `Class Feat 021`"
  is not noise — they are levels 21, 22 and 23.
- **Some blocks carry strays** from another feat series that shares the class name (5 in the Ranger's block 001, 10 in
  the Herald of Xotli's block 021). They are told apart by the stat ids they write: a class-base template only ever
  writes the five even attribute ids, `MaxHealth` and stat 280, never the **odd-id twins** `805/807/809/811/813/815`
  (which the client seeds to 100 — a percentage, not a score). Their attribute amounts are single digits rather than
  the base's multiples of ten.

`Tempest of Set` is stored in the RDB under the class's old name, **Stormcaller**.

#### Coverage and verification

**All 12 classes × all 80 levels** resolve, out of exactly 80 candidates each. Nothing about the layout is assumed: the
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

⚠️ Three level-1 ids that an earlier pass listed here (Herald of Xotli 3130632, Necromancer 1173784, Demonologist
1126680) were **back-computed** as `observed − (level − 1)` under the disproved id rule, not observed, and all three are
wrong — the arithmetic silently absorbed the unreadable ids in between (the Herald's by 2) or crossed into another block
entirely (the Necromancer's landed on level 15, in a block whose ids are 111,000 away from the rest of its run). Never
re-derive an observation; re-observe it — a level-1 character of the class brought in-game with the `GameCode/ItemData`
+ `ClientInterface` channels on, where the template loaded before the five attribute writes IS the base.

Shipped as the `class-attributes` importer module into `ClassAttributeBases`, and `WorldEntity.AttributeScore` returns
`classBase + gear + buffs`. A `(class, level)` with no row is treated as **unknown, not zero**: world entry logs a
warning naming the character, and that character's attributes are gear + buffs only.

## Wrong / unconfirmed ids (now tracked on the `CharacterStatId` enum)

- ✅ **RESOLVED (2026-06-14, in-client): the vitals current/max ids are `0x001B`/`0x01F8`/`0x01FA` =
  **current** and `0x0001`/`0x01F9`/`0x01FB` = **max** (Health/Stamina/Mana).** A 2026-06-09 pass had guessed
  these swapped (see the section above); the live `.setstat` probe settled it — `0x0001` moved the MAX health
  bar, `0x001B` moved current. See the (current, max) table above. Encoded correctly now in `CharacterStatId`
  (`Faolan.Core/Enums/CharacterStatId.cs`).
- **Attributes** `Strength 0x0324 / Constitution 0x0326 / Intelligence 0x0328 / Wisdom 0x032A / Dexterity 0x032E`:
  0x0324 and 0x0328 decode to 10 (→1.0 at ×10), not the sheet's Str 20 / Int 39; the others don't appear as
  clean records at all. The sheet **Attributes** (Str 20, Int 39, Con 56, Dex 79, Wis 26, Ferocity 0) are most
  likely **computed client-side** (base + gear + buffs) and not transmitted as plain stat records — searching
  for their values (20/56/39/26/79, or ×10) finds only mis-aligned/coincidental matches.

## Stat-block framing (resolved via the combat vitals packet)

The stat records are **count-prefixed**: a 1-byte record count, then N × 8-byte `[statId][value]` records. This
is clearest in the `0x96B8DC59` entity-vitals packet (`...<entityHandle><00000000><count:1><records>`, see
`docs/re/quests-and-combat.md`); the same record encoding is embedded in the larger `0x737A6DF9` snapshot.

## Who authors the login snapshot's sheet (#447)

`SnapshotStatSheet` — one place, for every character. It picks the baseline from provenance (an imported
character's real captured sheet, else `SubClassStatBlock.FreshSheet()`) and then applies the **same** override
table either way, by stat id:

| stat | source |
|---|---|
| `0xBF` PlayfieldToken | the live zone's minted token — session state, never the captured one |
| `0x0D7` GmLevel | the account type's GM privilege mask ([gm-commands.md](gm-commands.md)) |
| `0x36` Level | the character (a captured sheet's level is stale the moment it dings) |
| `0x34` Exp / `0x15E` MaxExp / `0x39` ExpCurrentLevelBase | the XP triple, authored together by `CharacterStatBlock.SetExperience` — see [The XP triple is cumulative](#the-xp-triple-is-cumulative) |

Before that there were two authoring paths patching different slots, so an override added to one silently did
nothing on the other — which is how the GM Level stat came to be set on the static fresh list and never reach
the wire for a single real login.

⚠️ **Word 80 of the stat block is `MaxExp`, not experience.** It carried a `CurrentXp` accessor with a "factor
10", written from the character's experience, which produced the right bytes for exactly one case by
coincidence: the beach template is a level-1 character whose MaxExp is 100, and the golden test carried
`Experience = 10` so `10 × 10` landed on it. The same golden now passes with that character's real, zero,
experience and MaxExp authored from the curve.

### The XP triple is cumulative

The three XP stats are a **running total and its two boundaries**, not a within-level amount:

| stat | meaning |
|---|---|
| `0x034` `Exp` | the character's running total |
| `0x039` `ExpCurrentLevelBase` | the running total at which the CURRENT level began |
| `0x15E` `MaxExp` | the running total at which the NEXT level begins |

so the client fills the bar as `(Exp - Base) / (MaxExp - Base)` and a level's **cost** is `MaxExp - Base`. Three
independent readings of the corpus say so: every captured snapshot has `Base <= Exp <= MaxExp`; a level-23
character carries `base=230240 max=263640` while a level-24 one carries `base=263640` — *the same number*; and the
`NewLevelIIR` ding's six "deltas" are `[Exp, Base, MaxExp, 1, 8, 0]`, where only the first varies between players
dinging to the same level. [V3: LevelingCurveCorpusTests.TheResolverReproducesEveryCapturedXpTriple]

`LevelingResolver` therefore stores **thresholds**, not costs, and derives `XpForNextLevel` as a difference. It
used to store the captured `MaxExp` values and hand them back as per-level costs, which made a level cost the
whole running total up to it — level 24 asked for 298,040 XP instead of its real 34,400, and
`CumulativeXpForLevel` summed the inflated values on top of that.

The curve is **server data**: the client ships none of it. There is no XP table among the 25 named `CoreTables`
data tables, `GAMETWEAKS` (RDB 1000516) has no level-threshold entry, and the exe's only XP strings are the three
UI value names it fills from these very stats. So the anchors are the levels a capture pinned — 1–6, 8, 9, 23–25,
80 (and 81, which is only ever a bar boundary) — and everything between them is linearly interpolated on the
cumulative curve. The character row keeps within-level progress; the base is added back when the triple is
authored.

The per-class vitals are still positional (`ApplyClassVitals`, slots 21/23/54/56/60/62/64/66/68 with their +148
copies) because several of them share a stat id within one list and the id alone cannot say which is which. That
`CharacterValueIndex` map and the `(statId, value)` sheet are two models of the same words, and where they have
been checked against each other they have disagreed — reconciling the rest is the remaining half of Gitea #447.

### `ChosenGod` (0x0A9) — a stat we author without knowing what it MEANS (#553)

The snapshot emitted a hard `0` here for every character. It varies in retail: 183 of the 184 captured snapshots
carry `0` and one carries `1`, so a fixed value is a character wearing someone else's number. `Character.ChosenGod`
now persists it and `SnapshotStatSheet` authors it, with `.chosengod <n>` as the setter. **That — a per-character
value being shipped as a constant — is the entire justification.** It is not a claim about what the number means.

⚠️ **The value is carried as an opaque wire number, with no deity name anywhere near it**, and there is no religion
system behind it to model:

- **Age of Conan has no player-choosable religion.** Deities are lore plus the three *priest classes'* patron gods
  (Priest of Mitra, Tempest of Set, Bear Shaman/Ymir). The AoC wiki has a "Choosing your Class" page and nothing
  for choosing a god. ⚠️ The "pick a religion, find a priest, become an acolyte" system a web search surfaces is
  **Conan Exiles — a different game.** #553 was opened on that premise ("AoC's religion is chosen during the
  Tortage storyline") and it does not hold.
- **It is not the priest patron either:** the one captured character carrying `1` is a *necromancer*.
- **And the client cannot be asked.** It *stores* this stat and never reads it back: `ConstScan` finds exactly two
  uses of `0xA9` as a stat id in the whole image and both are WRITES, there is no `GetStat(0xA9)` anywhere, and the
  one class that gives the id a dedicated slot (`FUN_00843d10` / `FUN_00843d40`, field `+0x244`) only mirrors it —
  see [`../re/aoc-client-stat-names.md`](../re/aoc-client-stat-names.md). It is a **server-side number**, so
  nothing the client draws depends on the value and no in-client check could confirm a meaning either.

Even calling `0` "has not chosen" reads the stat's *name* rather than any evidence.

## Open follow-ups
> Tracked as Gitea issues (see the `area:combat` / `area:aoc-systems` labels). The notes below are the technical context.

- ✅ ~~Locate real Max-Health / Max-Mana / Max-Stamina ids~~ — DONE (current/max pair table above).
- ✅ ~~Determine whether attributes are sent at all, or derived client-side~~ — **DONE (2026-06-15, #6):
  client-derived.** The sheet attribute values (NecroL80: Str 20 / Con 56 / Int 39 / Wis 26 / Dex 79, and ×10)
  appear **nowhere** as the value following their stat id (`0x0324`/`0x0326`/`0x0328`/`0x032A`/`0x032E`) across
  any NecroL80 capture — the only clean value the wire carries for those ids is a base `10` (= 1.0 at ×10). So the
  displayed attributes are computed client-side (base + gear + buffs), NOT transmitted as plain stat records.
  (Corroborating from the binary: the on-wire stat set is bounded by `g_Int32StatsOrder`, asserted
  `== generic_stats_size()` in `CoreCharacterFullUpdateIIR::ReadSubClass`; the array is built indirectly via
  vector growth, so enumerating its full id list needs the registration-call route, not a direct-write xref.)
- Combat (HP changes mid-fight) should reuse this block — see the combat work in [[archive_cpp_reference]] and
  `docs/tooling/capture-events.md`.
