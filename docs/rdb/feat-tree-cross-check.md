# Feat trees: an external cross-check of our decode

Our feat data is decoded from the client's own records by `RdbFeatImporter`, and until now nothing outside this
repo had ever agreed or disagreed with it. A community feat planner — <https://joharaoc.eu/feat>, one page per
class — turns out to publish the same structure in a form that **joins to our ids**, which makes it usable as an
independent oracle for feat-tree layout and effect magnitudes.

⛔ **It is not a wire source and never licenses sending anything.** Retail leads: the captures and the client are
the truth for anything that goes on the wire, and this is a third party's rendering of a client build that is not
necessarily ours. Its value is exactly one thing — telling us where our *content* decode disagrees with somebody
who did it independently, so the disagreement can be taken back to the client.

## The join

Each feat in the planner's page data carries a `hex` field. Read as hexadecimal it is **our
`FeatIdMaps.WireFeatId` for rank 0**, from which our other two identifiers follow (`BaseIndex = WireFeatId / 100`,
and `TemplateId` through `FeatIdMaps`):

| planner | ours |
|---|---|
| `hex` = `6476c` → 411500 | `WireFeatId` 411500 · `BaseIndex` 4115 · `TemplateId` 1151133 ("Archmage") |
| `hex` = `643e8` → 410600 | `WireFeatId` 410600 · `BaseIndex` 4106 · `TemplateId` 1151141 ("Mage Blood") |

Across all twelve class pages this resolves for **710 of 710** feats. That is the check that makes everything
below meaningful: two independent readings of the client landing on the same identifier space.

## What it confirms

**Tree layout: 708 of 710 agree on every structural field.** Comparing the planner against our
`FeatClassEntries` — which is the real client-derived structure, *not* `Feats.Tree` (a name-derived guess that
must never be used for this) — the required level, tree points, column and tier all match. The planner is
0-indexed where we are 1-indexed, so `col + 1 == TreeColumn` and `row + 1 == Tier`.

The two exceptions differ **only in required level**, with column, tier and points identical:

| feat | ours | planner |
|---|---:|---:|
| Winter's Bite | level 35 | level 30 |
| Enraged Spirit | level 15 | level 25 |

A pure level change with the position untouched is the signature of a balance pass between game builds, not of a
decode error on either side.

**Effect magnitudes reconcile, including the level scaling.** `FeatEffects` stores a low/high pair
(`Value` at `LowLevel`, `HighValue` at `HighLevel`) and the planner renders a single number per rank. Interpolating
ours linearly to **level 80** reproduces the planner exactly — worked through on Mage Blood (`StatId` 498,
`LowLevel` 20 → `HighLevel` 100):

| rank | ours @20 | ours @100 | interpolated @80 | ÷100 | planner |
|---:|---:|---:|---:|---:|---:|
| 1 | 297 | 1278 | 1032.75 | 10.33 | 10.3 |
| 2 | 409 | 1693 | 1372.00 | 13.72 | 13.7 |
| 3 | 510 | 2132 | 1726.50 | 17.27 | 17.3 |
| 4 | 601 | 2546 | 2059.75 | 20.60 | 20.6 |
| 5 | 708 | 2968 | 2403.00 | 24.03 | 24.0 |

So three things are confirmed at once: the stored per-rank values, the linear interpolation between the two
level anchors, and that the planner renders at level 80.

## ⭐ It independently confirms `StatDisplayScale` — 19 stats, zero disagreements

The `÷100` above is not universal: armour-type stats render at **×1**, and Spiritual Armament's per-rank values
equal our stored numbers with no division at all. That looks at first like the planner is an oracle for *which*
stat takes which scale — but we already have that answer from a better source. `StatDisplayScale`
(`Faolan.Core/Enums/StatDisplayScale.cs`) is the client's own divisor, read out of its formatter in #775.

So the planner is not a source here, it is a **check on that decode**, and it passes cleanly. Taking every feat
whose planner entry states per-rank numbers and which joins unambiguously to one `FeatEffects` row per rank
(38 feats), the implied divisor `ours@L80 / planner` was computed per stat and compared:

| divisor | stat ids (hex) | agrees with `StatDisplayScale` |
|---|---|---|
| ×1 | `0x0AA` `0x1C0` `0x1C1` `0x1C3` `0x36B` `0x3EF` | 6 / 6 |
| ×100 | `0x0FE` `0x1F2` `0x215` `0x220` `0x2C6` `0x36C` `0x36D` `0x36E` `0x380` `0x383` `0x385` `0x42F` `0x447` | 13 / 13 |

**19 of 19 agree, none differ**, and each stat's ratio is consistent across all its samples (the widest spread is
`0x380` at 99.27–101.78, which is rounding in the planner's displayed decimals). Two independent readings of the
client — one from the binary's formatter, one from a third party's rendering — landing on the same divisor for
nineteen stats is the strongest corroboration `StatDisplayScale` has.

⚠️ `0x0AA` (hate modifier) comes out at **−1**: the magnitude matches at ×1, but we store it negative where the
planner prints it positive with the minus in the surrounding prose. That is a sign convention, not a scale.


## What it surfaces that we do not have

**Feats with a stated per-rank effect and no row in any of our feat tables.** For Necromancer alone, ten of the
forty feats whose planner entry states per-rank numbers have nothing in `FeatEffects`, `FeatSpellGrants`,
`FeatSpellModifiers`, `FeatSpellStatModifiers`, `FeatProcs`, `FeatComboModifiers`, `FeatComboAttackModifiers` or
`FeatPerkCosts`: Grim Corruptor, Gluttonous Reaper, Empower Archmagus, Ravenous Souls, Embrace Death, The
Devourer, Wretched Heart Curse, Quickened Iron, Unleashed Power, and Ability - Tome of Erlik.

⚠️ Note what this is and is not. Of the forty, only eight sit in `FeatEffects`; the other twenty-two are in the
spell-modifier and proc tables, which is expected — most feats modify a spell rather than a stat. The ten are the
residue that is in none of them, and they are candidates for a gap, not proof of one.

**Name differences: 30 of 710**, in three groups that must be separated before any of them is acted on:

* **2 are spacing only** — `Spell- Dark Meditation` for their `Spell - Dark Meditation`, and a `To`/`to` case
  difference. Cosmetic on one side or the other.
* **14 are ours cut short**, and every one of them stops at an **ASCII apostrophe**: `Devourer's Might` stored as
  `Devourer`, `Don't Fear the Reaver` as `Don`, `At Death's Door` as `At Death`, `Smash 'n Crush` as `Smash `,
  and so on through `Ability - Cat's Paw`, `Combo - Grave's Blossom`, `Spell - Set's Life Spark`.
* the remaining **14 are genuinely different text** — some the planner's own typos where ours is right
  (Desecreation, Rejuvenanting Spirit, Copper Strom Chains), some a different name entirely (Improved Discipline
  vs Improved Fire Weave), some a `Spell -` vs `Ability -` prefix. Most likely a build difference.

⭐ **The cause is ours: we store the RDB template's INTERNAL name, not the client's display name.**

The client renders a feat's name from **LDB category 50220**, keyed by the feat template id — the same id
`Feats.Id` uses, so the join is direct:

| feat template id | LDB 50220 (displayed) | our `Feats.Name` |
|---|---|---|
| 780379–780383 | `Devourer's Might` | `Devourer` |
| 873300 | `At Death's Door` | `At Death` |
| 3514944–3514945 | `Don't Fear the Reaver` | the stem before the apostrophe |

`RdbFeatImporter.ReadNameAndDescription` is reading correctly — the RDB template really does carry the short
string. That string is an **authoring name**; where it coincides with the display name (most feats) nothing looks
wrong, and where the display name carries a possessive the authoring name is the stem, which is what made this
read as truncation at an apostrophe. There is nothing cutting anything.

⚠️ **A negative result is only as wide as the scope it was measured in.** This was first reported here as "our
build ships the short names, the planner is on a later build" on the strength of a scan of RDB type 1000020 that
found the long names absent — with `Nature's Revenge` as a present control. Both halves were true and the
conclusion still did not follow: *absent from the type searched* is not *absent from the client*, and the client
we ship against is the latest. The type scope that made the scan fast is exactly what narrowed its negative.

Scale, so far: LDB 50220 holds 84 entries containing `'s ` (≈17 feats, each rank being its own id) against 20
rows / 6 distinct names in `Feats`. The possessives are only the visible subset — any feat whose display name
differs from its authoring name at all is affected. Open work tracked in Gitea #925.

## ⭐ Why most name differences are NOT defects: the LDB label is deliberately shared

`--report feat-name-check` compares every shipped feat name against LDB 50220 and classifies what differs. The
result matters because the obvious fix — prefer the LDB string — is wrong on 96% of the population it touches:

| pairs / rows | pattern | verdict |
|---|---|---|
| 34 / 83 | ours is a strict **stem** of theirs (`Devourer` / `Devourer's Might`) | adopt theirs — **done** |
| 7 / 16 | differ only by a `Spell - ` / `Ability - ` / `Combo - ` prefix | adopt theirs — **done** |
| 88 / 91 | the **LDB entry is truncated** (`Spell - Dark Han` for `Spell - Dark Hand`) | keep ours |
| 778 / — | the LDB label is **shared by several feat ids** | keep ours |
| 15 | a unique LDB label that genuinely disagrees | open |

⛔ **The shared-label group is the important one, and it is by design.** A generic feat's authoring name encodes
class, subject, level band and rank — `Core Training: 1HB 20-40 3`, `Soldier: Shield Training 1` — while its
displayed label is short and reused across every rank, band and often class: `Basic One-Hand Blunt Training`
covers four ids, `Advanced Riding` eighteen, `HP` thirty. Adopting the label would collapse thirty distinct feats
onto one name and destroy exactly the distinctions our names carry. **Ours is the more informative string there,
and that is why the rule is a containment test rather than a preference.**

⚠️ The same shape, one level down, is why the rank tag must survive: our `HP 20-40 1`…`13` are thirteen rows the
displayed `HP 20-40` cannot tell apart, and `ParseName` reads the `Rank` column from that suffix.

The 15 unique-label disagreements are the only open residue, and one of them is a real loss on our side: six
`Gemcutter Novice` rows carry one name where the client names each crafting tier separately (`Apprentice`,
`Adept`, `Artisan`, `Expert`, `Master`, `Grandmaster`). The rest are renames in one direction or the other
(`Illumination` / `Vindicator`, `Improved Shield Bash` / `Improved Evasive Action`) with neither side
established. Open work tracked in Gitea #925.

## Reproducing

The planner's per-class page embeds its data as a `feats={...}` object in the page HTML; parse that, read `hex`
as hexadecimal, and join to `FeatIdMaps.WireFeatId`. Our side of the comparison is three tables:

```sql
-- structure
SELECT Level, TreeColumn, Tier, TreePointsRequired FROM FeatClassEntries WHERE FeatId = <WireFeatId>;
-- magnitudes, all ranks of one feat
SELECT m.Rank, e.StatId, e.Value, e.LowLevel, e.HighValue, e.HighLevel
FROM FeatIdMaps m JOIN FeatEffects e ON e.FeatId = m.TemplateId
WHERE m.BaseIndex = <WireFeatId / 100> ORDER BY m.Rank, e.Sequence;
```

⚠️ Joining `CharacterFeats.FeatId` straight to `FeatEffects.FeatId` matches nothing — they are different id
spaces and it returns zero rows rather than erroring. Go through `FeatIdMaps`.

The full 710-row comparison is committed beside this page as
[`feat-tree-cross-check.tsv`](feat-tree-cross-check.tsv) — one row per feat, carrying both sides' identifiers and
the per-field verdict, so the agreement figures above can be re-read without re-scraping.

Source: the JoharAoC feat planner (<https://joharaoc.eu/feat>), read 2026-09-02. Only the structural fields we
compare against are reproduced here; the numbers are facts about the game client, and the planner is credited as
the independent reading they were checked against.
