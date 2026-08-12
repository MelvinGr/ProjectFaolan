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
record bytes inside a parsed region : 8,175,543 / 8,241,205 = 99.2%
property-run field ids read         : 16 / 83
elements rendered as a sentence     : 51,849 / 61,840 = 83.8%
...or accounted for                 : 61,811 / 61,840 = 100.0%
```

⭐ **The second line is the honest one.** Five buckets, and only one of them is still our debt:

- **the client draws nothing either** — 9,124 elements over 83 kinds whose type id reaches no branch of
  `FUN_0149e450` at all, led by `DPSHit` (2,660), `StartServerSideScryScriptWithParams` (1,597) and
  `StartParticleEffect` (1,182). That is retail's behaviour, not a decode gap, and counting it as one read as
  ~5,000 elements of debt that do not exist;
- **a branch that PROVABLY has no 170 sentence** — 832 over 3 kinds, and the proof is per kind rather than a
  shrug (`GameActionTooltip.NoSentenceReason`). `Spell` (16) and `BCCCastSpell` (63) push category **100**, not
  170, and their tokens resolve there as plain label NOUNS — they draw a label, so there is no sentence to find.
  `InheritedComboAttack` (753) is the opposite shape: its sentence IS found (`PerSecond`, "%s per second") and
  the `%s` is a number the record does not contain — branch `0x014a103e` computes it as
  `key224 × 0.01 × [ESI] / [EBX]`, two floats out of the INVOCATION context. Rendering it would invent a number;
- **the TEMPLATE cannot fill it** — 6 elements over 2 kinds, and this one is per ELEMENT rather than per kind
  (`ReportCli.UnrenderableReason`), because it is one ARM of a branch whose others render perfectly well. Both
  entries are measured: a `HitPercentage` in mode 1 or 2 renders the CASTER's own key-1 stat (a guard — no
  shipped element takes it, and without the guard such an element would fall through to the stored-value arms
  and draw a confidently wrong number), and an element whose Stat slot is present and **zero** names no stat at
  all, so there is nothing for the sentence's stat slot to hold. `Storyteller` (4695200) is the whole of the
  second case: one element, `Modify 0=0 39=0`;
- **a branch but no sentence we can find** — 29 elements over 1 kind, all of them the tail of
  `ChangeInheritedWeaponAttack` whose hate / damage-type clause is deliberately not drawn (below);
- **a sentence whose slots are not bound** — **empty**. This was 1,121 elements over 8 kinds and is the whole of
  #782's second half; the bindings that closed it are `AddAbsorb`, `UpgradeCombo`, `AddSpellProc`,
  `ChangeRecastTime`/`ChangeLineRecastTime`, `CastingLevelModifier` and `HitPercentage`'s damage arm, each
  written up below.

Nothing half-binds: a sentence with a conversion the binder cannot fill prints as its raw key list instead, and
`LdbFormat.TryApply` fails rather than leaving a stray `%s` in a line that would otherwise read as confident.

⚠️ **`LdbFormat`'s conversion scan has to be STRICT about what may sit between the `%` and its letter**, and was
not. `AddAbsorb`'s sentence ends `#4%.0f% of the damage dealt` — a single `%` where the author meant `%%` — and a
loose scan (forward to the first `s`/`d`/`f`/`i`/`u`) read `% o` plus the **`f` of "of"** as a fifth conversion, so
a fully bound four-slot sentence failed to render and the kind read as unbindable. A `%` that is not followed by
`[flags][width][.precision]<conv>` stands for itself.

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
rows; a token's call site then attributes to the branch it falls inside (the greatest branch target ≤ the site).
Result: **`docs/re/gameaction-tooltip-kinds.tsv`, 53 call sites onto 30 kinds.**

⚠️ **The dumper used to report only the NEAREST token before each getter call, and that lost one arm of every
conditional pair.** A branch that picks between two tokens loads both before the getter runs:

```
JLE  <decrease arm>
MOV  EDX,0x208d480          ; "UpgradeCombo_IncreaseDamage"   <- fall-through arm
...
MOV  EDX,0x208d49c          ; "UpgradeCombo_DecreaseDamage"   <- the one a backward scan reaches first
PUSH 0xaa
CALL 0x0060f2f0
```

so `UpgradeCombo_IncreaseDamage` / `…IncreaseCooldown` appeared in no dump at all, and the artifact listed only
the two `Decrease` halves — which reads as "this kind only ever decreases". `DumpTooltipTokens.java` now emits
every token pointer in the window (filtering strings containing a `%`, which are the composing branches' printf
glue rather than token names), and the regenerated file is a strict SUPERSET of the old one: every previously
attributed token still lands on the same kind.

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

### `ChangeInheritedWeaponAttack` (53250): a header enum plus four optional clauses

The same composing shape with more parts. The **header** is picked by key 84, and it is a two-value enum —
anything else bails out of the branch entirely (`CMP EDI,1 ; JNZ 0x014a22e4`), so such an element draws nothing:

| key 84 | token | English |
|---|---|---|
| 0 | `FinishingComboAttack` | "Finishing attack" |
| 1 | `OpeningComboAttack` | "Opening attack" |

Then one clause per metric, each gated on **its own** key being non-zero:

| key | tokens | English |
|---|---|---|
| 66 | `Increase`/`DecreaseComboDPS` | "Increase Damage per Sec %s" |
| 67 | `Increase`/`DecreaseComboCriticalChance` | "Increase Critical Chance %s" |
| 68 | `Increase`/`DecreaseComboHateAdjustment` | "Increase Hate Adjustment %s" |
| 69 | `ChangeComboDamageType` | "Changes damage type to %s" |

⚠️ **`Increase` is the DEFAULT; `Decrease` is taken only when the driver is NEGATIVE.** The client loads the
Increase token first and overwrites it under `JNS` (jump if not sign), so **zero takes Increase**. Writing the
test as `> 0` instead of `>= 0` inverts every clause whose driver is 0 — which, given the next paragraph, is all
of them. `Annihilate`'s own description ("Substantially **increases** the critical strike chance of the
conqueror's next combo") is what caught it.

⛔ **The client picks the CRIT clause's increase/decrease from key 66, not key 67** — `TEST EBX,EBX` at
`0x014a1fe3`, where `EBX` still holds the DPS delta fetched at `0x014a1de5` and nothing has reloaded it. Read
literally that is a **client bug**: a positive key 66 with a negative key 67 would draw "Increase Critical Chance
-N". It is reproduced rather than corrected, because retail is authoritative — and it is **unobservable** either
way: key 66 is **0 on all 796 shipped elements** (key 67 is non-zero on 767), so the DPS clause never fires and
the crit clause always takes its Increase arm.

⚠️ The crit value is a **float**, unlike the DPS clause's int: `CVTSI2SS` off key 67's slot then
`MULSS [0x020b5944]`, and that constant is **0.025**. So `Annihilate`'s stored 1000 draws as `25`.

⚠️ Each clause is a separate **line** in the client (each ends in a `FUN_010d1850` push onto a string vector),
not one sentence. `--report spell-tooltip` draws one line per element, so it joins them with `"; "` — that
separator is the report's presentation, not something the client ships.

⚠️ The HATE and DAMAGE-TYPE clauses are gated but **not rendered**, deliberately: no shipped element carries
either key, so the hate scale is unmeasured and the damage-type `%s` has no resolved name space. An element that
grew one is left unrendered so the census says so, rather than drawn with an invented number — which is the 29
elements still in that bucket.

### `AddSpellLineProc` (53070): it COMPOSES its line, so no sentence lookup could ever have found one

It read as a kind with three recovered tokens and no sentence. It is a kind that **builds** its line, and none of
its three tokens is a sentence — they are fragments:

```
OnSelf  "on self"      OnTarget  "on target"      TimesPerMinute  "Procs per Minute"
```

Branch `0x0149f69e` reads `GetIntStat(0x463)` = key 88 (the spell run on SELF), `0x4f4` = key 236 (the one on the
TARGET) and `0x4f5` = key 237 (the rate), then concatenates using three **literal** formats that live at the push
sites rather than in the LDB:

| literal | address | role |
|---|---|---|
| `%s %s` | `0x20614a4` | one side — "⟨spell⟩ on self" |
| `" %s "` | `0x208d330` | the joiner — " and " |
| `%s, %d %s` | `0x208d2e0` | the wrapper — "⟨sides⟩, ⟨rate⟩ Procs per Minute" |

⚠️ **The joiner's word comes from a THIRD getter and a different CATEGORY.** `MOV ESI,0x208ce00 ; CALL
FUN_0060eb50` fetches the token `And`, and it resolves in **category 100** ("and"), not 170.
`DumpTooltipTokens.java` anchors on the two category-170 getters (`FUN_0060ec00`, `FUN_0060f2f0`), so `And`
appears in no token dump — it is a common WORD, from the same category as the `Spell` / `Combo` / `Ability` label
nouns. Worth knowing before reading any token dump as exhaustive.

⚠️ The wrapper is **unconditional**: the rate clause is appended whether or not either side is present (only the
trim before it is conditional), so a proc with no spell at all still draws its rate.

Rendered, `Aflame` (4414105) — whose own description is *"adds a spelline proc to all arrowspells"* — comes out as
`Aflame (4357732) on target, 20 Procs per Minute`.

### `AddPercentSkillModifier` (53189): the sentence is chosen by whether its two STAT slots are equal

One of the kinds #782 named as unreachable by the generic binder — it has no `Stat` (key 0) slot at all, so the
`(Stat, magnitude)` rule cannot touch it and it sat in the "no sentence we can find" bucket. It has **two**
sentences, and branch `0x014a1b52` picks between them by comparing its two stat slots:

```
ESI = GetIntStat(0x315)   ; key 66
EDI = GetIntStat(0x316)   ; key 67
EBX = GetIntStat(0x2f9)   ; key 39, the percentage
CMP ESI,EDI ; JNZ 0x014a1c95         <- equal -> same-stat arm, else different-stat arm
```

| arm | token | English |
|---|---|---|
| equal | `AddPercentSkillModifierSameStat` | `#2{1: Increases \| Decreases } your #3%s by #1%s` |
| differ | `AddPercentSkillModifierDifferentStat` | `#2{1: Increases \| Decreases } your #4%s by #1%s of your #3%s` |

⭐ **The slot order is read off the binds, not guessed.** Both arms append the value string, then the `SETG` of
the value's sign as the `#2` branch selector, then the stat saved at `[ESP+0x10]` (key 66); the different-stat arm
appends one more from `[ESP+0x24]` (key 67). So **`#3` is key 66 and `#4` is key 67** — "increases your ⟨67⟩ by
N% of your ⟨66⟩", i.e. 66 is the SOURCE stat and 67 the one being raised.

⭐⭐ **And the shipped data confirms that direction independently, in prose.** `Arcane Plunder` (4281011) carries
`66=804` Strength / `67=490` with `39=2..10`, and its own description reads *"Increases the rogue's **Immunity
Rating** based on the sum of **Strength** and **Dexterity**"* — which is exactly what the binding renders
("Increases your Immunity Rating by 2% of your Strength"), and the reverse of what swapping `#3`/`#4` would.
`Absolute Philtre of Constitution` is the other arm: `66 = 67 = 806`, `39 = 8` → "Increases your Constitution by 8%".

⚠️ The magnitude is a **plain** percent here (8, 13, 2..10) — not the hundredths `ChangeHitPercent` (53030) and
`UpgradeCombo` (53209) carry. The sentence has no literal `%`, so the suffix belongs to the value string, which is
what the branch's own value formatter appends.

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
`SpellTemplate_t::GetMagicSpell` and returns one bit of it. The same predicate gates `ConeFormula`, which skips
its whole sentence when its effect spell has the bit (falling into the shared tail, so a periodic element still
draws its "every N s" suffix and nothing else).

⭐ **`+0xCB` is byte 3 of the dword at `+0xC8`** — the SAME spell flags word #629/#630 already reads bit 2 of for
the mana mode, i.e. record word 13 / byte 52. So the flag needs no new field: it is **bit 24 of a word we were
already reading**. `--report spell-tooltip census` measures it: **3,111 of 21,502 records = 14.5%** carry it.

⚠️ **The renderer flags it rather than acting on it.** Only bits 1–2 of that word are proven to reach the client
verbatim, and suppressing on an unproven bit makes lines *disappear* — the failure a screenshot diff cannot see,
where over-drawing is the one it can. So an element whose referenced spell carries the bit renders normally with
a `⚠️ retail draws nothing here` marker. Flip it to a real suppression once it is confirmed in-client. Open work
and the falsified hypotheses are in Gitea #789.

### `AddAbsorb` (53046): four slots in sentence order, and the first is a STAT

Branch `0x0149eaed`, keys `[0 Stat, 70, 33, 34, 88]`, fetched in that order and appended in it:

| slot | key | what |
|---|---|---|
| `#1%s` | 0 | the absorbing STAT's name |
| `#2%.2f` | 70 | ×0.01 — points of damage absorbed per point of the stat |
| `#3%.0f` | 33 | ×0.01 — the per-attack cap, in points of damage |
| `#4%.0f%` | 34 | ×0.01 — the same cap as a share of the attack |

⭐ **What pins `#1` as the stat rather than the end spell** is the lookup the branch uses for it:
`FUN_0060f0e0(0x267, …)` passes key 0's value in `ESI`, and `0x267` = **615** is the client's stat-name category —
the same `FUN_0060ebb0(0x267, statId)` that `HitPercentage`'s arms call. The shipped data agrees from the other
side: `Sustaining Faith (Rank 2)` absorbs into **Mana** and `Claws of Stone (Rank 2)` into **Health**.

The second line — `AddAbsorbEndSpell` filled with key 88's spell — is only drawn when that spell is present and
does not carry the hide-from-tooltip bit (#789). The report draws one line per element and does not compose it.

### `UpgradeCombo` (53209): two independent clauses, and BOTH signs have a token

Branch `0x014a1817` is a composer, not a sentence lookup. The
`Upgrade combo: %s, damage modifier: %d, recharge adjust: %d` sitting at its own type id is a leftover the client
never fetches.

| clause | gate | tokens | unit |
|---|---|---|---|
| damage | key 66 (`0x315`) | `UpgradeCombo_{Increase,Decrease}Damage` | `%` (`0x1fbcf18`) |
| cooldown | key 67 (`0x316`) | `UpgradeCombo_{Increase,Decrease}Cooldown` | `""` (`0x2c23bf8`) |

Both magnitudes are HUNDREDTHS (`CVTSI2SS ; MULSS [0x01f2800c]`), and the number is drawn by `FUN_0149a5c0` —
`<font color="%s">%+.Nf%s</font>`, where the colour is `tooltip_positive` / `tooltip_negative` (`FUN_0149a6c0`)
and the precision comes from the value: `%+.2f` when it has no integer part and a fraction under 0.05, `%+.0f`
when it has an integer part and a fraction under 0.05, `%+.1f` otherwise.

⚠️ **The token is picked by `JLE`, so ZERO takes the DECREASE arm here** — the opposite of `53250`, which loads
Increase first and overwrites it under `JNS`. The two kinds genuinely disagree; neither reading generalises.

⚠️ And the two clauses set the colour flag on OPPOSITE arms: `[ESP+0x20]` is cleared on *decrease* for damage and
on *increase* for cooldown, which is the semantics (a longer cooldown is the bad direction). It only reaches the
value-modifier annotation, which is empty without a viewing character.

`Disarming Aggression` renders `+4% Increase to Damage Per Second` at rank 1 and `+8%` at rank 2.

### `AddSpellProc` (53211): a computed CHANCE plus up to two sides

Branch `0x0149f3f7`, another composer — its three tokens are fragments (`ChanceOf`, `OnSelf`, `OnTarget`) glued
by literal formats at the push sites: `"%.0f%% %s "` for the chance, `"%s %s "` per side, and `"%s "` around the
`And` that LDB category **100** supplies (the same joiner `AddSpellLineProc` uses).

⭐ **The chance is computed, not stored.** `FUN_0149bc20` reads key 237 (the rate) and key 187 (a SPELL), loads
that spell and takes its property **294** — the cast time in centiseconds, the field the importer already reads —
then returns

```
min(100, castSeconds × rate × 1.67)          1.67 = 100/60, i.e. procs-per-minute -> per-cast percent
```

⚠️ When the key-187 spell is missing or its cast time is ≤ 0 the client substitutes a literal **50 centiseconds**,
so an unresolvable reference still draws a number rather than zero.

`Degradation of Set (Rank 2)` states 200 procs/minute on a 3-second cast → `3 × 200 × 1.67 = 1002`, capped to
**100% chance of Degradation of Set (Rank 2) (205647) on target**. The cap is not always hit: the same binding
renders 8%, 42% and 53% elsewhere in the catalog.

### `ChangeRecastTime` (53031) / `ChangeLineRecastTime` (53069): one slot, and it is a DURATION

Branch `0x014a02b4` (shared — 53069 draws with 53031's sentence) reads key 39 and hands the raw centiseconds to
`FUN_00807ae0`, which is the client's duration formatter rather than a number-to-string:

| `|centiseconds|` | what it draws |
|---|---|
| `< 1` | the empty string |
| `> 12000` | a d/h/m/s breakdown (`FUN_005451e0` splits by 86400/3600/60) under the literal `%s%s` |
| whole seconds | LDB **100** `TimeFormatSeconds`, with an INT |
| otherwise | LDB **100** `TimeFloatFormatSeconds`, with a float |

⚠️ The `<font color="tooltip_positive">` is baked into the LDB **string**, so a negative delta still draws in the
positive colour — `Stunning Bolt` renders *"Modify recast time by -20 seconds"*. ⚠️ The report does not compose the
over-two-minutes arm: its own sentence is unread and no shipped recast delta reaches it.

### `CastingLevelModifier` (53223): the client fills two of the sentence's three slots

Branch `0x0149fdda` appends exactly two arguments to `Change hit percent by #2%d%% for spell #1%s #3%s` — the NAME
behind key 0 (`FUN_008d76b0` resolves the id through the spell catalog, reads its property 23 and looks that up in
LDB **50210**) and key 39 as an int. `#3` is never supplied by the client either, so it draws empty. One shipped
element carries this kind: a dev record naming `Test Nuke`.

### `HitPercentage` (53007): the damage arm's second slot is a DAMAGE SCHOOL

The Health-damage arm hands key 71 to `FUN_0149a8f0`, and that resolver is **two lookups in two categories**:

```
if (type - 1 > 13)                      ; i.e. type outside 1..14
    return LDB170[elf28("GenericDamage")]
sentence = LDB170[elf28("DamageType")]
name     = <category 600>[type]         ; 1 slashing 2 piercing 3 crushing 4 fire 5 electrical
return sentence filled with name        ;   6 holy 7 unholy 8 poison 9 siege
```

⭐ **Category 600 is what made this look undecodable.** The previous note recorded that `elf28("DamageType<n>")`
misses in category 170 for every `n` in 0..14 — true, and beside the point: the per-school word was never in 170
at all, and `FUN_00a8f470` (the hash the resolver calls on its way in) is plain **elf28** over the single literal
`"DamageType"`. This is also an independent naming of the damage schools, from a different client source than the
combat-side numbering.

### Finding a spell that carries a kind

`--report spell-tooltip --report-arg kind:<typeId>` lists the spells carrying one GameAction kind with the line
the renderer makes of each. Reading a branch is half the job; the other half is a shipped record to check the
reading against — `SpellAreaEffects.RadiusMetres` is what settled `ConeFormula`, and `Annihilate`'s own prose is
what caught an inverted sign on `53250`.
