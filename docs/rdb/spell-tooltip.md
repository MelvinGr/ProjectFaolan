# Rendering a spell's tooltip out of the RDB

A spell's in-client tooltip is the strongest accounting check we have on the spell decode: it consumes nearly
everything a record holds, and a screenshot is an unarguable oracle. `--report spell-tooltip <spellId>` renders
one out of our own data, beside the raw record it came from; `--report spell-tooltip census` scores the same
renderer over the whole catalog.

The renderer is the **client's**, not ours — four pieces, each recovered from the client:

| piece | source |
|---|---|
| the per-kind SENTENCE | **LDB category 170**, keyed by GameAction type id or by a token hash (below) |
| the RESISTANCE kind | LDB category 55200, the client's spell-effect / metatype catalog (`7` is "Root") |
| the stat NAME | LDB category 615, keyed by the wire stat id |
| the wire→display DIVISOR | `StatDisplayScale` — `FUN_0149a300`'s divisor selection (#251) |
| the UNIT suffix | `StatUnitSuffix` — `FUN_01119f20` (#775) |

## LDB category 170 is the GameAction sentence catalog

Nothing had read category 170 before #775. It holds one format string per rendered GameAction, and it is keyed
**two ways**:

- **by the GameAction TYPE ID itself** for ~30 types — id `53088` is
  `%s %.2fm%s radius up to %d%s targets`, id `53226` is `%.0f seconds`, id `53045` is `#2%s #1%s`;
- **by the ELF-28 hash of a TOKEN NAME** for the rest. The tooltip builder `FUN_0149e450` pushes the category
  and a static `char*`:

```
PUSH 0xaa
MOV  ESI,0x208d1d4          ; "IncreaseAOERadius"
CALL 0x0060ec00             ; GetLocalisedString(category, token)
```

and `elf28("IncreaseAOERadius") == 22958195`, which is the shipped *"Increases the area of effect by %d
meters"*. **All 30 token names recovered from that function's 48 category-170 call sites hash onto an id the
category actually ships, with no misses** — `docs/re/gameaction-tooltip-tokens.tsv`, dumped by
`tools/ghidra/DumpTooltipTokens.java`.

⚠️ The hash is 28 bits, so a match is evidence and not proof (see `Elf28`). 30 for 30 against a 130-entry
category is far past coincidence; a single new token is not.

### …and a THIRD way: a kind can borrow another kind's sentence

`FUN_0149e450`'s dispatch is two MSVC index+target table pairs (`0x14a2e90`/`0x14a2de8` over `0xCF0A..0xD002`,
`0x14a324c`/`0x14a323c` over `0xD013..0xD025`), plus the compares that bracket them. **54 of the 260 registered
types reach a branch of their own; the other 206 fall to the default and the client draws nothing for them** —
which is what `GameActionTooltip.HasTooltipBranch` answers, and why the census counts those separately. Every branch is transcribed in
[`../re/gameaction-tooltip-dispatch.tsv`](../re/gameaction-tooltip-dispatch.tsv) with its alias, its tokens and
the order it reads its slots in.

Some branches open with a literal `MOV ECX,<another type id>` before the category-170 fetch — they draw
themselves with a *different* type's sentence. That is `GameActionTooltip.SentenceKinds`, and it is why several
very common kinds looked like they had none at all:

| kind | draws itself as | elements |
|---|---|---:|
| `InterpolatedModify` 53220 | `Modify` 53045 | 3,596 |
| `SpellLineStatModifier` 53062 + 53098/53099/53283/53285 | `SpellStatModifier` 53054 | 1,156 + |
| `InterpolatedTempChangeSkill` 53221 | `TempChangeSkill` 53110 | 152 |
| `ChangeLineRecastTime` 53069 | `ChangeRecastTime` 53031 | 5 |

The interpolated ones are the instructive case: the branch reads keys 174/175/0, lerps them against the caster's
stat `0x3C5` (Apprentice Level), and hands the RESOLVED number to the flat twin's sentence. The ramp never
appears in the text.

### ⭐ And a sentence can be chosen by the element's own VALUES

Two families pick their string from what they carry, which is why "which sentence" cannot be answered before the
slots are read:

- **the damage/heal family** — `Hit` 53002, `SpecialHit` 53196, `InterpolatedHit` 53219 — goes through
  `FUN_0149aa30(buf, stat, min, max, flag)`, whose head IS the rule:
  `stat == 0x1B Health ? (min < 0 ? DamageHealth : HealHealth) : DamageOtherStat`, i.e. *"%s points of %s"*,
  *"Heals %s"* or *"Removes %s points of %s"*. The value slot is the RANGE `min-max`, which is why these carry
  two magnitudes where every other modifier carries one. The `%s %s %s` sitting at 53002's own type id is not
  what the client draws.
- **the hit-percent pair** — `ChangeHitPercent` 53030 and `ChangeLineHitPercent` 53075 — reads key 39 and picks
  `IncreasesHitPercent` when positive, `DecreasesHitPercent` otherwise, rendering the value ×0.01 with a `%`
  (branch `0x014a018c`, the float at `0x01f2800c` is literally `0.01`).
- **`HitPercentage` 53007** picks between FIVE tokens on three things at once — which stat, the sign, and a mode
  word (branch `0x014a0b99`): Health → `HitPercentHeal` / `HitPercentDamage`, Stamina drained → `HitPercentDamage`
  with that stat's name, anything else → `HitPercentHealStatByOtherStat` / `HitPercentDamageStatByOtherStat`
  ("Renews 20 percent of Mana based on Mana"). ⚠️ Its MAGNITUDE is **raw** — the client passes a literal `0` as
  the range builder's divide flag where `Hit` passes the stat's own percent predicate, so scaling it would report
  a 30% drain as 0.3%.

⚠️ **An equal-endpoint range collapses to one number**, and that too is the client's branch rather than a tidy-up:
`FUN_0149a700` compares its two arguments and takes a separate path when they match, so a flat magnitude draws as
`20`, never `20-20`.

The format grammar is printf plus two things printf has no notion of, both handled by `LdbFormat`:

- `#N` before a conversion selects **argument N** rather than the next in sequence — the German and Korean
  pages reorder their slots, which is what the marker is for;
- `#N{1: a| b}` is a plural/branch form, arm 1 when argument N equals 1.

## The unit suffix is NOT the display divisor

`StatDisplayScale` answers "what does the client divide the wire value by"; `StatUnitSuffix` answers "what does
it write after the result". The two sets genuinely differ and **both** are needed to render one modifier:

| stat | divisor | suffix | wire | drawn |
|---|---|---|---|---|
| `0x386` Slashing Invulnerability Rating | 100 | `%` | `-1000` | `-10%` |
| `0x348` DamageMultiplier | 1 | `%` | `-5` | `-5%` |
| `0x001` HealthMax | 100 | *(none)* | `+3000` | `+30` |

`FUN_01119f20` is the suffix helper: five ranges, four of them MSVC dense switches (an index byte table plus a
dword target table — `tools/ghidra/DumpSwitchTable.java` composes the pair) and the fifth a run of plain
compares. It appends one of three literals — `"%"`, `"m"`, or the empty string. Only **two** ids take the metre
suffix, `0x0DE SpellColumnRadius` and `0x11F AttackRange`, and both are spell-record property fields, which is
what makes the third unit worth carrying at all.

## ⭐ A tooltip is a chain, and the numbers are not on the spell

`Gangrenous Stench` (221070) is the worked case (in-client screenshots on #772). Everything the player reads is
reproduced except the mana, and **almost none of it is on the spell's own record**:

```
Gangrenous Stench (221070)
  cast 1.5 s   duration 25 s   recast 60 s          <- 294 / 8 / a SetSpellRecastTime element
  Gangrenous Stench (221069) 7.00m radius up to 12 targets      <- FilteredAreaFormula
      Run effect Physical Wrack (3963058)                       <- CastNano
          -10% Slashing Invulnerability                         <- 4x Modify, TWO links down
          -10% Piercing Invulnerability
          -10% Crushing Invulnerability
          -10% Poison Invulnerability
      -5% Damage Multiplier
      Temp change Natural Health / Mana / Stamina Regen to 0
      Gangrenous Stench (221068) 4.00m radius, every 3 s        <- AreaFormula + the element's 0x2d6 period
```

A renderer that does not FOLLOW the chain reproduces none of it. That is exactly why the tooltip is a better
gate than any per-record assertion: it fails unless the whole graph is walked.

Two decodes it exercises that a flat read would not:

- the **recast** is a `SetSpellRecastTime` / `SetSpellLineRecastTime` ELEMENT, not a property field. Field 210
  `Recharge Delay` is 0 on Gangrenous Stench (whose recast is 60 s) and 100 on `Life Leech` (whose
  `SetSpellLineRecastTime` says 3000 cs = 30 s), so 210 is not the cooldown.
- the element's `0x2d6` is the **aura PERIOD** in centiseconds — a repeating element and a one-shot element are
  the same kind with the same keys, and the difference is only there (#772).

## What the census measures

```
record bytes inside a parsed region : 5,311,375 / 8,241,205 = 64.4%   (3,785,151 in elements, 1,526,224 in the property run)
property-run field ids read         : 16 / 111
elements rendered as a sentence     : 47,632 / 61,840 = 77.0%
...or accounted for                 : 56,756 / 61,840 = 91.8%
```

⭐ **The second line is the honest one.** Three buckets, and only two of them are our debt:

- **the client draws nothing either** — 9,124 elements over 83 kinds whose type id reaches no branch of
  `FUN_0149e450` at all, led by `DPSHit` (2,660), `StartServerSideScryScriptWithParams` (1,597) and
  `StartParticleEffect` (1,182). That is retail's behaviour, not a decode gap, and counting it as one read as
  ~5,000 elements of debt that do not exist;
- **a branch but no sentence we can find** — 2,157 over 7 kinds (`ChangeInheritedWeaponAttack` 795,
  `InheritedComboAttack` 753, `AddSpellLineProc` 424);
- **a sentence whose slots are not bound** — 2,927 over 12 kinds, led by the two cone kinds, `AddComboProc` and
  the two `HitPercentage` arms that need live caster state.

⚠️ **The cone kinds are deliberately unbound, and the reason generalises.** `%.2f%s degree arc, %.2fm%s radius`
reads like two slots off the element and is not: branch `0x014a28f9` fetches the CHARACTER, adds keys 11 and 213
to the cone she already has, clamps, and prints the DIFFERENCE. The element states a DELTA. Binding it to the raw
slots renders `Cyclone of Steel` as "4.00 degree arc, 2.00m radius" — plausible, and wrong.

Nothing half-binds: a sentence with a conversion the binder cannot fill prints as its raw key list instead, and
`LdbFormat.TryApply` fails rather than leaving a stray `%s` in a line that would otherwise read as confident.

## What it cannot produce

- **The mana number.** Field 132 is usually a PERCENT of the caster's pool (#629), so the client's "913 mana"
  needs the caster. The report prints the mode and the stored value.
- **A level ramp's value** without a level — `--report-arg <id>@<level>` supplies one; without it the ramp
  prints as its two endpoints.
