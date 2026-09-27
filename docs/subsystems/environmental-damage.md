# Environmental damage — falls, hazard volumes and the death penalty

The damage the world deals, as opposed to the damage a creature deals through `CombatResolver`. Fall damage and
lava are one piece of plumbing: a non-combat damage source.

`EnvironmentService` owns the decisions and reaches the wire only through `IEnvironmentOutbound`. It is driven
from the **`CharDCMove` handler**, not the world tick, because both triggers are edges on the player's own
position stream: a fall's size is known only on the record that reports the landing, and a hazard's own
client-named trigger is `playerenter` — the crossing, not "being inside".

## The wire form: `StatDamageIIR`, environmental variant

Retail sends environmental damage as a `StatDamageIIR` (`0xE9F24184`) with a **different grammar** from the
combat one, which is why `StatDamages.Environmental` is its own value factory beside `StatDamageCodec`:

```
[subop][class][target][env=0][u32 amount][0][0][0][u32 statId][u32 0][i32 -amount][u32 school][0 ×9]
```

| difference | combat form | environmental form |
|---|---|---|
| first word | the target's resulting health | the **damage magnitude** (the delta is exactly its negation) |
| count word | `2` | `0` |
| caster / spell tail | class marker, caster handle, effect idx, spell id, trailing `1` | **all zero** — the world is not an entity |
| school | 0–10 (`DamageSchool`) | **12**, outside the combat range |

That the first word is a magnitude and not a resulting value is proven by consecutive falls: in
`chat-annotated-3` three falls seconds apart carry 50994 → 14076 → 219125, which cannot be a health track
(nothing heals 2050 health in 7 s, and the second record's own delta implies a start the first contradicts).

**No `DamageResultIIR` accompanies it** — a fall renders no floating combat number. Do not pair this with
`SendDamageNumber` the way the melee path does. [V3: CombatDamageCodecTests.EnvironmentalDamage_IsByteIdentical]

## Fall damage

Detection needs no physics: the client says whether it is off the ground in every move record. `flag1` clears
`NoAccelerationVector` while airborne, because it starts sending an acceleration vector the instant the character
leaves the ground. `EnvironmentService` is an edge detector over that bit.

The `Airborne` bit (`0x40`) is **not** the test, despite its name: measured across the fall captures it is set
only while the character is rising (`flag1` `0x42`) and clears for the descent (`0x02`), so keying on it misses
every fall that starts from a ledge rather than a jump — which is most of them.

Damage is **a fraction of max health**, not a flat number — see `FallDamage` for the full derivation over all 28
corpus events across four characters whose pools span 263 to 8241.

**What it is a fraction of is the client's own reported impact speed, squared.** The move record that
lands carries the vertical velocity; `EnvironmentService` keeps the fastest downward one seen during the airborne
run, and that is what the law is applied to:

```
damage = maxHealth × 0.00067221 × (v² − 13.96²)          v = impact speed, world units per second
```

The **fallback**, for a landing whose move carried no velocity block, is the same law in the other unit, applied
to the drop measured from the airborne run's **apex** (seeded with the last grounded height, so a step off a ledge
— which has no rising samples at all — still measures from the top):

```
damage = maxHealth × 3.8% × (fallDistance − 3.08)
```

**Two things about that switch, both easy to get backwards.**

- It is **not** a new trust assumption. The server's own drop is reconstructed from the client's position stream,
  so the velocity in the same packet is the *same source*, one field over — and without the ~200 ms sampling loss
  that costs a fraction of a unit at each end and dominates a 4-unit fall. Over the corpus's 34 paired falls, `v²`
  explains the damage at **r = 0.957** against the sampled drop's **0.625**, and the median error is 21% against
  40% (the drop refitted on this same corpus, i.e. its best case).
- The law is fitted **in v², never converted to height**. Height needs a gravity, and gravity can only be measured as `v²/2h` against the sampled drop — the
  very quantity being replaced (p10 7.6 / median 19.3 / p90 36.2 over the deep falls). `frac = a(h−h₀)` and
  `frac = A(v²−V₀)` are the same model with gravity absorbed into the slope, so fitting in `v²` never needs it.
[V3: FallDamageTests.SpeedLaw_BeatsTheHeightLaw_OnTheSameFalls]

The debuff that rides with it comes from a two-tier ladder, applied atomically with the damage:

| tier | spell | duration | stat effects |
|---|---|---|---|
| **Bruised Body** | 3744225 | 5 s | none at all — a pure marker |
| **Broken Bones** | 3745876 | 10 s | Evade Chance −5000, Casting Concentration −800 |

Both ship in `faolan.db`, so applying them through the ordinary buff path gets their effects for free. The tier is chosen from the damage as a fraction of the pool, which separates the observations where raw
fall distance does not. [V3: FallDamageTests.DebuffTier_ReproducesEveryCorpusObservation_ExceptTheOneInversion]

## Hazard volumes

A hazard is a region that hurts or kills on entry. The **geometry is client data** — imported from the RDB
bounded-area table into `MapRegion`, exactly, with no tolerance — while the **behaviour is ours**. That split is
structural rather than a convention: `MapHazard` is a **1:1 side table on the region** carrying only
`EffectSpellId` / `Damage` / `Lethal`, so importing a volume's geometry does not imply it is a hazard and the other
hazard families can be imported as inert volumes. Full evidence, format and the survey of everything else in
that table: [`docs/rdb/bounded-areas-1000053.md`](../rdb/bounded-areas-1000053.md).

The decoded case is lava, whose whole sequence the annotated capture shows end to end:

1. apply spell **3773837 "Incineration"** (3000 ms) — the word the player's own annotation used
   (`got incinerated by lava`). Distinct from the `Incinerate (1..5)` family, which is a Herald of Xotli ability.
2. the player dies — with **no `StatDamage` at all**, which is why `Lethal` is its own column rather than
   "damage ≥ health".
3. a **death penalty** lands (below), then a respawn teleport.

Containment is a point-in-polygon walk with a Y-range and AABB pre-filter. The AABB is only a reject filter: the
volumes are concave ribbons tracing a flow, so their bounding boxes cover a lot of safe ground.

## The death penalty

**The tiers stack — they are not one debuff that escalates.** In `chat-annotated-4` the playfield reload at
20:24:36 bulk re-applies the surviving buffs with their remaining durations, and **Death Penalty 1**
(1,261,960 ms left of 1,800,000) and **Death Penalty 2** (1,775,260 ms left) are in the same batch — two
independent countdowns from two separate deaths. The lava death then adds **Death Penalty 3** on top.

So the "tier" is just how many penalties you already carry: your Nth concurrent one is the spell named
"Death Penalty N" (2544 → 2543 → 2542, all 30 min). Nothing tracks a death counter — the live buff set *is* the
counter, which is also why the penalty fades on its own as each stack lapses. All three rows carry the same
per-stack effects: Magic Damage Modifier −100, Damage Multiplier −1, Damage Mitigation −100.

Retail applies the new penalty **atomically with stripping every other buff** (one batch at 20:24:08 carries
the `Death Penalty 2` apply and the end-twins of all four buffs the player held). So in `TickPlayerDeaths` the
apply must come after our own death-strip, or the strip removes the penalty it just granted.

A fourth death inside 30 minutes adds nothing — there is no "Death Penalty 4" anywhere in the corpus.
[V3: DeathPenaltyTests.FourthDeath_AddsNothing]

## Knockback

Not environmental strictly speaking — it is a creature ability — but it lands in the same place: the server moves
the player without the player asking, and it shares the "displace a victim, then pin it" plumbing.

**There is no knockback message.** The displacement is a `MoveTaskIIR` in **mode 0x05**, a *forced move*, addressed
to the victim's own handle:

```
[u16 0][from vec3][0x05][0x00][3 floats 1.0, 1.0, 0.0][anchor vec3][to vec3][0x00]     65-byte body
```

What identifies it is rarity. In the annotated dungeon capture there are **1,219** s2c `MoveTaskIIR`s; only **11**
name the player as *subject*, nine of those are trivial 31–32 byte anchors, and exactly **one** is this long form —
at the instant the player's own annotation says `knockbacked`. A **`SetPosIIR`** pins the destination 1.7 s later,
and that record appears **twice in the whole capture**: world entry, and here.

The batch also applies spell **4784832**, which the database names **"Knockback immunity"** (10 ms) — the mechanic
naming itself, and the strongest single confirmation in the window.
[V3: KnockbackTests.ForcedMove_IsByteIdenticalToTheCapturedKnockback]

### The second knockback settles the geometry

`annotated_3` carries an inbound knockback on the player from a Wharf Rat, at the second the player's own
annotation says `got knockedback` [V3: KnockbackTests.ForcedMove_IsByteIdenticalToTheSecondCapturedKnockback].
Two independent events, different attackers, different sessions — and they agree:

| | displacement | attacker→victim |
|---|---|---|
| first (`chat-annotated`) | 3.01 | — |
| second (`annotated_3`) | **3.000** | **3.255 → 6.255** |

So the two questions one head-on sample cannot answer are answered. The distance is a **constant 3.0**, not
something scaled per ability or per level; and the push is along the **attacker→victim ray**, because the
attacker-to-victim distance grows by exactly the displacement — a push along the attacker's *facing* would not
have to.

The second sample also shows the batch is bigger than the immunity buff. In one instant retail sends
`Knockback immunity` **and** `Recently Impaired` on the victim (once as a 50-byte apply, three times as a 46-byte
variant), `DispatchAddSpellResistance [0x1AB][0x64]`, and **`Successful Knockback`** (`0x10B1A6`) applied to the
**attacker** as a self-marker — then the forced-move `MoveTask`, then the `SetPosIIR` 1.7 s later. We send the
immunity buff and the move pair; the rest are decoded but unwired, because `Recently Impaired` reads like a
general impair diminishing-returns marker rather than a knockback record, and wiring it here would be a guess
about a system nobody has decoded.

The packets are decoded and reproduced byte-exact, and the geometry has two measurements — but the three floats
are deliberately unnamed (duration/speed/gravity are all equally
plausible), and `KnockbackResistance` (0x040D) is read but its scaling curve is **invented**: both captured
victims' values are unknown, so no curve is derivable.

**What triggers it.** The finisher of a `knockback`-category player combo (see [`combos.md`](combos.md)), and the
GM command `.knockback [distance]`. The captured knockback came from an NPC ability, but the `ApplySpell` in that
batch is the immunity guard rather than the ability that did the knocking, so the corpus does not say which NPC
attacks knock back — and guessing would put a shove on attacks that never had one.

## Scope

Only the lava hazard family is imported; the other hazard families the client ships (traps, spikes, drowning,
poison fog, …) have no wire evidence for what they do. Open work tracked in Gitea #504, #495 and #496.
