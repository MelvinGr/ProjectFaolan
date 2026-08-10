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
elements rendered as a sentence     : 49,438 / 61,840 = 79.9%
...or accounted for                 : 58,562 / 61,840 = 94.7%
```

⭐ **The second line is the honest one.** Three buckets, and only two of them are our debt:

- **the client draws nothing either** — 9,124 elements over 83 kinds whose type id reaches no branch of
  `FUN_0149e450` at all, led by `DPSHit` (2,660), `StartServerSideScryScriptWithParams` (1,597) and
  `StartParticleEffect` (1,182). That is retail's behaviour, not a decode gap, and counting it as one read as
  ~5,000 elements of debt that do not exist;
- **a branch but no sentence we can find** — 2,157 over 7 kinds (`ChangeInheritedWeaponAttack` 795,
  `InheritedComboAttack` 753, `AddSpellLineProc` 424);
- **a sentence whose slots are not bound** — 1,121 over 8 kinds, led by `HitPercentage`'s live-state arms,
  `UpgradeCombo` and `AddSpellProc`.

Nothing half-binds: a sentence with a conversion the binder cannot fill prints as its raw key list instead, and
`LdbFormat.TryApply` fails rather than leaving a stray `%s` in a line that would otherwise read as confident.

## What it cannot produce

- **The mana number.** Field 132 is usually a PERCENT of the caster's pool (#629), so the client's "913 mana"
  needs the caster. The report prints the mode and the stored value.
- **A level ramp's value** without a level — `--report-arg <id>@<level>` supplies one; without it the ramp
  prints as its two endpoints.


## The token -> KIND attribution (#782)

`docs/re/gameaction-tooltip-tokens.tsv` says which TOKEN each call site pushes; it does not say which GameAction
KIND reaches that call site, because the builder's per-type dispatch is a dense MSVC switch and neither of its
two tables means anything alone. Composing them closes that:

```
SUB   EAX,0xcf0a                          ; FUN_0149e450
CMP   EAX,0xf8
MOVZX EAX,byte ptr [EAX + 0x14a2e90]      ; index table
JMP   dword ptr [EAX*0x4 + 0x14a2de8]     ; target table
```

`DumpSwitchTable.java` with `BASE=cf0a COUNT=f9 BYTES=14a2e90 TARGETS=14a2de8` yields 249 `type id -> branch`
rows; a token's call site then attributes to the branch it falls inside. Result:
**`docs/re/gameaction-tooltip-kinds.tsv`, 33 of the 36 tokens placed onto 22 kinds.**

⚠️ **`kind -> token` is one-to-many BY CONSTRUCTION.** Several kinds dispatch to one branch, and a branch picks
between its tokens on its own inner conditions — `53250` reaches five (`OpeningComboAttack`,
`DecreaseComboDPS`, `DecreaseComboCriticalChance`, `DecreaseComboHateAdjustment`, `ChangeComboDamageType`). So a
row is the CANDIDATE SET for that kind, not a single sentence, and picking within it needs the branch's own
conditions read.

The three tokens that do not place (`DamageHealth`, `HealHealth`, `DamageOtherStat`) sit in the SHARED formatter
`FUN_0149aa30` rather than in a branch, so they are reachable from many kinds — absent by design, not a gap.

⭐ **A cross-check falls out.** The branch for `53054` `SpellStatModifier` / `53062` `SpellLineStatModifier`
pushes **`IncreaseItemDelay`** — and the client's internal stat-name table calls stat `294` `ItemDelay` where
LDB 615 displays it as *Casting Time (Seconds)* (see `docs/re/aoc-client-stat-names.md`). Two independent client
sources agreeing that the delay those modifiers move is the cast time.


### The branches that push a DIFFERENT category (#782)

Not every branch reads category 170. Seven call sites push **`0x64` = category 100** instead, and their tokens
resolve there as plain label NOUNS rather than sentences:

| token | LDB 100 |
| --- | --- |
| `Spell` / `Mount` / `Pet` / `Perk` / `Combo` / `Ability` | *Spell* / *Mount* / *Pet* / *Perk* / *Combo* / *Ability* |
| `LearnSpell` | *Learn Spell* |

⭐ That is the answer for the census bucket *"kinds WITH a branch but no sentence we can find"* on **`53019`
`Spell`** and **`53061` `BCCCastSpell`**: their branches build a line out of those nouns plus a resolved spell
name, so there is no category-170 sentence to find. Absent by construction, like the 206 kinds that reach the
switch's default.

### Which keys each branch reads — and why that is NOT the slot order (#782)

Every branch that still had an unfilled sentence is disassembled into
`docs/re/gameaction-tooltip-slots.tsv`: the keys it fetches (`MOV ECX,<statKey>; CALL 0x0077a510`, in address
order) beside the record's own declared key list. On every kind measured the branch fetches a strict SUBSET of the
declared list, and three controls say the extraction is right — `53002` and `53250` reproduce their `statKeys`
column exactly, and `53046` comes out as `[722, 793, 755, 756, 1123]`, character-for-character the key list #711
documents from the other direction.

⚠️ **What it does NOT give you is the argument order, and the obvious shortcut is wrong.** The branch fetches its
keys into stack locals and then APPENDS them from those locals in a different order: `53259`'s fetches sit at
`0x2317` / `0x2374` / `0x23c4` while its appends read `[ESP+0x10]`, `[ESP+0x18]` and `[ESP+0x50]`.

`53259` `ConeFormula` is the case that falsifies it, and it has independent ground truth. Its element is
`88=<spell> 212=-60 213=120 11=6`, and `SpellAreaEffects.RadiusMetres` for the same spell is **6** — key 11. So
the radius slot takes key 11 and the arc takes key 213, the OPPOSITE of the fetch order. Binding by fetch order
would render `Wave of Life` as *"6.00 degree arc, 120.00m radius"*. Filling these sentences therefore needs the
local-to-slot trace, which that file deliberately does not claim to contain.

⚠️ **The two cone kinds are different branches, and an earlier note grouped them under one.** The second dispatch
table (`SUB EAX,0xd013`, tables `0x14a324c` / `0x14a323c`) puts `0x014a28f9` on **`53273` alone**; `53259`
dispatches to `0x014a2316` off the `CMP EAX,0xd00b` / `JZ`. Both are read out below, and neither is blocked on
live state.

### ⭐ Every magnitude is appended TWICE, and the second one is the `%s`

The extra `%s` after each `%.2f`/`%d` is not a unit — the units are literal text (`%.2fm%s radius`). It is the
**value-modifier annotation**. A branch appends the resolved number and then
`FUN_014a3880(resolved - raw, <unitToken>)`, which is `FUN_014aa410`:

- the difference is zero → it assigns `DAT_02c23bf8`, the **empty string**;
- otherwise it builds `<font color="tooltip_positive|tooltip_negative">` around
  LDB170(`ValueModSignPositive` / `ValueModSignNegative`) + the difference + the unit token.

So a tooltip drawn with no character modifier in play has an empty string in every one of those slots, and that
is why the renderer binds `""` to each of them. It also explains a family of tokens that never looked like
sentences: `ValueModUnitMeters`, `ValueModSignPositive`, `ValueModSignNegative` are the annotation's pieces.

### ⭐ …and the arithmetic around them collapses to the raw key

Both numbers go through the client's value helpers before being drawn, and in this build both terms vanish:

- the **multiplicative** term is `FUN_014a9f90`: `(GetStat(0x57B) * 0.1 * 0.01 * w + 1.0) * value`, where `w` is a
  per-call float global — `0x02D25E34`, `0x02D25E54`, `0x02D25E64`. ⭐ **All three are `0.0` in the image and the
  only references to them anywhere in `AgeOfConan.exe` are READS, from `FUN_0149e450` itself** — nothing writes
  them, so the term is exactly `1.0` and the helper is the identity. (Its every early-exit path returns `value`
  unchanged too, so there is no branch where it is anything else.)
- the **additive** term is `FUN_014a9c10`: `value + f(character->0x2D8)` when a viewing character has a modifier of
  index 6, `value` otherwise. That part genuinely needs live state — and it is exactly the part the `%s`
  annotation reports, never the number.

⚠️ Anyone reading these branches for a divisor should stop at the zero rather than transcribe it: the two `FLD`s
of `0x02d25e34` / `0x02d25e54` are runtime-initialised defaults being pushed as arguments, not scale constants.

### The CONE kinds (53259 / 53273): what their slots take

Traced through the branches' stack locals rather than inferred from the fetch order.

| record key | logical | what it is | evidence |
| ---: | ---: | --- | --- |
| `1123` | 88 | the effect spell — 53259's leading `%s`, built by `ItemInfoCreator_c::MakeSpellLink` | `FUN_014a3950` formats `<a href="spellref://%d/">%s</a>` |
| `1245` | 213 | the **arc in degrees** — the first `%.2f` | clamped to `[0, 360]` against the `360.0` at `0x020b5270`, and it is what retail draws (below) |
| `733` | 11 | the **radius**, metres — the second `%.2f` | `SpellAreaEffects.RadiusMetres` is 6 for `Wave of Life (Rank 2)` and its key 11 is 6 |
| `1244` | 212 | the arc's negative half (always `-213/2`) | **not fetched** by either branch |

⭐ So the arc fills the sentence BEFORE the radius while both branches FETCH the radius first — the concrete
reason fetch order cannot stand in for slot order, and a swap nothing would have caught, both slots being `%.2f`.

**Ground truth, from outside our own decode.** Retail's mount-ability text is documented publicly as *"Mammoth
Sweep (A 140 degree arc 8m radius knockback in front of you)"*, *"Rhino Sweep … 90 degree arc 6m radius"*,
*"Trample … 60 degree arc 5m radius"*. Our render off the RDB:

```
Mammoth Charge (3777189)              140.00 degree arc, 8.00m radius
Mount Ability: Rhino Charge (3115122)  90.00 degree arc, 6.00m radius
Mount Ability: Cavalry Charge (3113912) 60.00 degree arc, 4.00m radius
```

The arcs match exactly, and `140` is not reachable from any reading of the pair other than key 213 verbatim
(`212` is `-70`). That is what settles the arithmetic the previous pass left open.

⚠️ **53259 and 53273 are different branches** (`0x014a2316` / `0x014a28f9`) and only 53273 fetches the character
(`FUN_006f03b0`) — 53259 gets its context from a vtable call on the singleton at `0x0297d374`. Both hand that
context to the same helpers above, so neither is blocked on live state. 53273 also has **no** key-88 read and no
spell link, which is why its sentence is the same line minus the leading `%s`: **the append count matches each
sentence's slot count, 4 against 4 and 5 against 5**, which is the structural check that the two are paired the
right way round.

### `ColumnAddTargets` (53249): a different getter, and a x0.01

⚠️ An earlier revision of the slot dump recorded this branch as reading **no keys**. That was an artefact of
anchoring on `MOV ECX,<key>; CALL 0x0077a510`: it reads keys 11 and 238 through **`FUN_014af090(record, key)`**
instead, which takes the key as a pushed argument.

⚠️⚠️ And that getter ends `CVTSI2SS; MULSS XMM0,[0x01F2800C]` — it **divides the stored integer by 100** where
the int getter every other branch uses hands the value back verbatim. A stored `50` is a half-metre column, not a
50 m one, and the `%.2f` makes the 100× version look like a finished line rather than a failed bind. Bound
correctly the widths come out as a clean per-rank progression: `1.00m`, `1.25m`, `1.50m`, `1.75m`, `2.00m`.

The branch also picks its sentence by value: it compares `|key 238|` against the `0.005` at `0x01f27378`, drawing
its own type-id string `%.2fm%s wide column to target` when there is no distance and the
`ColumnAddTargetsWithDistance` token — which appends the distance and a second annotation — when there is.

One more exception worth knowing: **`53007`** reads key `754`, which its record does not declare at all —
corroborating the existing note that this branch *"reads all four slots plus key 32"*.

### `AddComboProc` (53212): the sentence is chosen by which SIDE is present

Branch `0x0149ec8e` reads key 88 (the proc run on SELF), key 236 (the one run on the TARGET) and key 237, and
picks one of three tokens by which of the two spells is there — `%s on self and %s on target`, `%s on self`,
`%s on target`. The `Add Combo Proc. Run #1%s on self and #2%s on target. Triggers #4%d times per minute.` sitting
at its own type id is not what the client draws, and its `#1 #2 #4` numbering against three key reads is exactly
the mismatch that says so.

⚠️ Key 237 (procs per minute) is appended as a third argument that none of the three token sentences has a slot
for, so it never appears — the same shape as `Knockback`'s key 191.

⭐ **A spell can suppress its own tooltip line.** The presence test is not `id != 0` but
`id != 0 && (spell->0xCB & 1) == 0` — `FUN_014a3b30` loads the referenced spell through
`SpellTemplate_t::GetMagicSpell` and returns one bit of it. The same predicate gates `ConeFormula`, which draws
**nothing at all** when its effect spell has the bit set. Which stored field populates that byte is open work,
tracked in Gitea #789; until then the renderer uses `id != 0`, which can only draw a line retail omits, never a
wrong number in one.
