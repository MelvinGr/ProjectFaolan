# The spell record's property fields ARE stat ids

RDB type **1040005** (the spell catalog, 21,502 records) is a 12-byte header followed by a fixed HEAD, then a run
of `[fieldId u32][value u32]` LE pairs in strictly INCREASING field order. **83 distinct field ids occur.**

⚠️ **It was 111 until the run stopped being found by a heuristic (#632).** The old rule took the LONGEST ascending
run anywhere in the record, and 28 of those "field ids" were regions that are not the property run at all —
`3 Attack Speed` (1,207 records), `23 Static Instance` (1,203) and `76 Item Class` (1,167) are the record's own
HEAD words, read as a 3-pair run because their ids happen to ascend. Any figure derived from the old count is
inflated by those; the named-field tallies below were checked against the corrected set.

## ⭐ The record, whole (#632)

It is one **tagged field stream**, and word 4 states its own length. A field's value is a u32, a string, or a
`0x3F1`-framed list:

```
[u32 classTag = 51051][u32 a][u32 15][u32 23][u32 (pairs + 1) * 0x3F1]
pairs x [u32 fieldId][u32 value]                          scalar fields
[u32 21][u32 33][u16 nameLen][u16 descLen][name][desc]    the two TEXT fields
[u32 22][u32 36][frame][1-word items][frame][criteria]    the two LIST fields   (1,871 records)
element lists                                             [2][listId][frame] groups, #785
```

⭐ **The frame word at byte 16 is load-bearing**: the string block sits at exactly `20 + 8 * pairs`, on
**21,502 of 21,502 records**. So the property run's extent is stated by the record rather than searched for, and
the search is kept only as a fallback (one record carries the `[21][33]` pair twice, so a search that wins could
anchor on a description's bytes).

The `pairs` count spans the head's own pairs, the ascending property run, and any trailing resource reference -
which is why `frameCount - propertyFields` is exactly the head-pair count, 5 / 4 / 3 for the 60 / 52 / 44-byte
heads.

⭐ **The two LIST-valued fields carry CRITERIA.** Field 22's items are one word; field 36's are three, and they
are `[id][value][operator]` - the same triple the elements carry (#785/#790), with the same operator vocabulary
(35 distinct, max 166, against the client's own `0..0x401` bound in `FUN_006e9f20`). ⚠️ Variable length - 32
bytes on 1,338 records but 24, 44, 56 and 80 on the rest, so a fixed-32 read finds a third of them.

Accounting over the whole catalog: **99.2%**, and `Hellfire Stream` (8600) is at **100%, zero bytes
unaccounted**.

## What the rest of a record is (#632)

That run is only ~13% of a record, and #632 was filed on the other ~87% having "never been looked at". It is
five things, and the accounting now reaches **99.2%** of all 8.2 MB (was 64.4%):

| region | bytes over the catalog | where it is decoded |
|---|---:|---|
| the element lists | 3,785,151 | [`../re/gameactionlist-grammar.md`](../re/gameactionlist-grammar.md) |
| the **name + description block** | 1,261,636 | below |
| the head + property run | 2,808,488 | this page |
| the list-valued fields | 70,764 | this page |
| the element-list headers | 249,504 | `[2][listId][(n+1) * 0x3F1]`, #785 |

### The name/description block

It sits **immediately after the property run** — which is why the ascending-run reader stops in front of it: the
next two field ids carry TEXT rather than a u32.

```
[u32 21][u32 33][u16 nameLength][u16 descriptionLength][name][description]
```

LATIN1, unterminated, back to back. A zero description length is normal; a zero name length is not. 21,459 of
the 21,502 records carry one, and `RdbSpellImporter.ReadStringBlock` reads it.

⭐ It is the record's OWN text rather than the LDB's, which matters for the **1,594 spells LDB category 50210
has no name for**: `Hellfire Stream` (8600) renders as *"spell 8600"* out of the LDB and the record says
`Hellfire Stream`, with its full description. `--report spell-tooltip` falls back to it and marks the line
`[from the record]`.

### ⭐ The head, and why its length matters

The head is **variable**: 60 bytes on 20,538 records, 52 on 959 and 44 on 5. Read against the property run's
start, the last five words are constant in shape —

```
[flags][298][0][88][0]   then the property run
```

— and `298` / `88` hold catalog-wide, which is what pins the FLAGS word to `runStart - 20`. Everything earlier is
`[51051 classTag][a][15][23][b][76][0][30][c][0]`, where `15`, `23`, `76`, `0`, `30` and `0` are catalog-wide
constants and the optional `[76][0]` pair is what makes the short head short. ⭐ `b` is not data at all - it is
the **frame word** above, `(pairs + 1) * 0x3F1`, which is why it took 28 values that are all multiples of
`0x3F1`. The head's five leading pairs are `(76, 30, 0, 298, 88)` — so the FLAGS word is the value of field `0`
and `c` the value of field `30`, ordinary fields that simply sit in front of the ascending run. What genuinely
carries data is `a` (6 distinct values, a block count — below), `c` (17) and the flags word (1,364).

⚠️ **The flags word was read at a FIXED `record byte 52` until #632**, and the head being variable-length means
that lands on the wrong word for the short ones — it returned **0 on 936 records**, forcing their mana cost to
read as absolute and their hide-from-tooltip bit to read as clear whatever the record said. Fixing it moved 20
spells' `ManaCostIsPercent` and 78 spells into the hidden set. `RdbSpellImporter.ReadFlagsWord` is the one place
that locates it.

### The flags word's bits

`--report spell-flags` sweeps it bit by bit against twelve predicates — the DB's columns plus, from the record
itself, whether it carries a proc element, any criterion, any element at all, or a `CastNano`. ⭐ **Bit 2 is the
control**: it is a known predicate (`ManaCostIsPercent`, #630) and it reproduces that column exactly, which is
the yardstick every other row is read against.

| bit | records set | reading |
|---:|---:|---|
| 2 | 1,778 | **mana cost is a percent of the pool** (#630) — the control |
| 24 | 3,189 | **hide from tooltip** (#789) — from the client's own `FUN_014a3b30` |
| 16 | 11,610 | **gates the timed-effect path** — the client only computes and applies a duration behind it (#632, below) |
| 25 | 185 | a strict SUBSET of the duration-carrying records (185/185) — but the client reads this bit **nowhere**, so it is a property of the data, not of the protocol |

### ⚠️ The raw agreement percentage is the wrong measure, and it inverted the answer

Two mostly-false columns agree on every record that has neither, so a raw percentage **rewards rarity**. The
report used to warn about that and then print percentages anyway; it now prints the **excess over chance** —
what a bit of this bit's own rarity would score against this predicate's own prevalence, subtracted off. That
collapses almost the whole table to nothing and leaves two entries:

```
bit  2 vs mana%     +15.6 points     <- the known definition, the yardstick
bit 16 vs duration  +48.4 points     <- three times it
```

⭐ **So bit 16 is a far stronger signal than the control, not a weaker one.** `mana%` is an 8.5%-prevalence
predicate and `duration` a 56% one: 99.07% agreement on a balanced predicate says much more than 100% on a rare
one. The old rule of thumb — *"99% is a lead, 100% is a definition"* — is only valid between predicates of equal
prevalence, and these are not.

⚠️ **It is still not the plain duration predicate.** Broken out, the residual is asymmetric — **62** records set
the bit with no duration, and **131** state a duration with the bit CLEAR, which is the direction that
disproves it (`VisualBuffTest`, `Avert Thine Eyes (Rank 1-3)`, `TRIGGER Environmental Damage: Frost High`). And
it is not a threshold: field 8's median is 1,550 on the agreeing set against 1,300 on the disagreeing one, over
the same range. Measured against the record's OWN field 8, so it cannot be a `DurationMs` column blend.

### ⭐ The route in is the CLIENT, and bit 16 is NAMED: it GATES THE DURATION

The client keeps the flags word at `spell + 0xC8`. #789 found bit 24 as `MOV BL, byte ptr [EAX + 0xCB]; AND BL, 1`,
so **bit 16 is `byte [spell + 0xCA] & 1`** — and `FUN_007ee710`'s bit-set branch is what it is for:

```c
if (spell != 0 && (*(byte *)(spell + 0xCA) & 1) != 0) {     // <- bit 16
    FUN_007ed310(local_68, param_1, in_EDX[1]);
    ...
    iVar7  = (**(code **)(... + 0x10c))(0x1d0, 2);          // GetStat(0x1D0 DurationModifier)
    fVar1  = (float)(int)(iVar7 * local_60);
    local_74 = (longlong)ROUND(fVar1 * 0.01);               // the applied duration
}
```

**Stat `0x1D0` is `DurationModifier`** (the client's own stat-name table), the result is scaled `× 0.01` and lands
in a `longlong` — a time. So bit 16 gates the **timed-effect path**: the branch that computes and applies a
duration at all.

⭐ **That turns the 99.07% from a correlation into a mechanism, and it explains the residual's asymmetry** — the
thing the sweep could not do. The **131 records that state a duration with the bit CLEAR** are not
counterexamples: their field 8 is stored and *never applied*, because the code that would apply it is behind the
bit. `VisualBuffTest` heading that list is exactly what a never-applied duration looks like. And the 62 the other
way are a bit set over a zero duration, which multiplies out to nothing.

⚠️ **This is a naming, not a proof of exhaustiveness.** Six functions in the magic subsystem test the byte
spelling (`FUN_007ea480`, `FUN_007ebe70`, `FUN_007eddc0`, `FUN_007ee710`, `FUN_007f43a0`, `FUN_007f4df0`, plus
`FUN_008da660`); the duration computation above is in one of them, and the others are guards on the same
condition rather than second meanings.

### The bits the client READS AT ALL — the census, and why it needed a second spelling

[`DumpFlagBitReads.java`](../../tools/ghidra/DumpFlagBitReads.java) enumerates every bit of the word the client
reads, anywhere in the image. That is a different question from the predicate sweep and a more basic one: **a bit
nothing reads is not a decode gap**, while a bit read in fifteen places is a lead with fifteen contexts attached.

⚠️ **A bit test has two spellings, and a census that knows only one lies.** The client writes both, for the *same*
bit:

```
MOVZX EAX, byte ptr [ESI + 0xCA] ; AND AL,1                    <- byte form, bit 16
MOV   EAX, dword ptr [ESI + 0xC8] ; SHR EAX,0x10 ; AND EAX,1   <- shift-and-mask, the same bit
```

A scan for the byte form alone finds 14 sites for this whole word; most of it is read the other way. Walking both,
**21 of the 32 bits are read somewhere**, and the count per bit is the ranking:

```
bit  0  x1     bit  1  x17    bit  2  x7     bit  3  x3     bit  4  x1
bit  5  x4     bit  6  x4     bit  7  x3     bit  8  x7     bit 11  x1
bit 14  x4     bit 15  x15    bit 16  x4     bit 19  x1     bit 20  x5
bit 21  x2     bit 23  x1     bit 24  x3     bit 26  x2     bit 29  x1     bit 30  x1
```

**Bits 9, 10, 12, 13, 17, 18, 22, 25, 27, 28 and 31 are read nowhere** — including bit 25, which the predicate
sweep flagged as a clean 185/185 subset of the duration-carrying records. A bit the client never reads cannot
mean anything to the client, so that entry is a property of the DATA (a subset relation among stored values), not
of the protocol.

⚠️ The tool cannot know the base register holds a spell — `+0xC8` is a common displacement — so read the
**addresses**, not the counts. The magic subsystem's own range (`0x0076xxxx`–`0x008Dxxxx`, plus the tooltip
builder at `0x0149xxxx`–`0x014Axxxx`) is where the known bits 2, 16 and 24 all land, and it is the range a new
lead has to land in before it is about this word at all. Bits 1 (x17) and 15 (x15) are the next two worth
reading, and both have most of their sites there.

### ⭐ Read: bit 16 gates RE-INSTANTIATING a magic on the SPAWN path

`FUN_007f43a0` is `CoreMagicHolder::Magic` (it names itself in its own log channel), and its one caller is
**`FUN_01458D40` — the SimpleChar spawn apply**, the same function that copies `npc_stats` field by field
(#797/#800). It is handed the spawn message's magic list and walks it; per magic it looks the spell record up,
logs `SpellItem_t <id> is missing from the RDB` when it cannot, and otherwise:

```
else if ((*(byte *)(spell + 0xCA) & 1) != 0) {           // bit 16 SET
    SetStat(0x96 /* 150 CastingLevel */, magic->level);
    apply(..., (*(uint *)(spell + 0xC8) >> 0x14) & 1 /* bit 20 */, ...);
}
```

The other two consumers take the CLEAR branch, and both are about what the effect reaches: `FUN_007F4DF0` adds
the **position** entries of a target list to the effect only when the bit is clear (entities are added either
way), and `FUN_008DA660` aborts on a failed reachability check only when it is clear.

⭐ So the mechanism behind the association is: **bit 16 marks a magic the client RE-CREATES when the entity
streams in** — a persistent effect rather than an instant one. That is why it tracks "carries a duration" so
closely, and it accounts for the residual going both ways: a permanent effect states no duration and still has to
be re-applied (the 62), while a timed effect with nothing to re-instantiate states one without the bit (the 131).

⚠️ Still a reading. No predicate partitions the bit, and the reading is inferred from what the three consumers do
with it rather than from a name. What it does settle is that the bit is about the effect's LIFETIME on the
client, not about its cost, school or targeting.

⚠️ It also bears on Gitea #677 (buffs on an entity streaming into view): the spawn body's magic list is the
channel a streaming observer needs, and bit 16 is what says which of those magics the client will actually
re-create from it.

### ⭐ Word 0 is a CLASS TAG — and the whole head is generic, not a spell head

`0xC76B` (51051) appears **exactly once** in `AgeOfConan.exe`, in `FUN_0076bef0` — slot 23 of
`SpellItemIF_c`'s vtable:

```c
void FUN_0076bef0(int this, undefined4 *out) {
    out[0] = 0xc76b;                   // this class's TYPE TAG
    out[1] = *(undefined4 *)(this + 0x3c);
}
```

⭐ **Slot 23 is a `(typeTag, id)` resource-key accessor, swept across every RTTI vtable in the image**
(`DumpVirtualSlotAcrossClasses.java SLOT=0x5c`). Its overriders are the shortcut-bar item-interface family —
`SpellItemIF_c`, `ACGItemIF_c`, `ACGDynamicItemIF_c`, `ComboItemIF_c`, `SpecialItemIF_c`, `AttackDirectionIF_c`,
`StatItemIF_c` (pure virtual). `ACGItemIF_c` passes its own `+0x3c` to an item lookup and returns the pair at the
live item's `+0xC0/+0xC4` — the ACG **template pair** — while `ComboItemIF_c` / `SpecialItemIF_c` /
`AttackDirectionIF_c` all return the shared zero pair. So `+0x3c` is *the referent's id* and the key is
`(what kind of thing, which one)`.

⚠️ **That means the key's second word is the SPELL ID, not the record's word 1.** The spell id sits in the RDB
entry header at body `+4`, outside the record; only word 0 coincides with the tag. The earlier reading —
"the record opens with exactly those two words, so `a` is a member at `+0x3C`" — does not survive the sweep.

⭐ **The head is not the spell catalog's.** A FEAT record (RDB type 1000020) has it word for word with a
different tag:

```
[u32 rdbType][u32 id][u32 1000000]                         the 12-byte RDB entry header
[u32 classTag][u32 a][u32 15][u32 23][u32 (pairs+1)*0x3F1]  ← identical on 1040005 and 1000020
```

`51005` (`0xC73D`) on feats against `51051` on spells, same `15`, same `23`, same frame word, same five leading
pairs `(76, 30, 0, 298, 88)`. ⭐ And the tag is **not even constant within type 1040005**: 11 records carry
`30047` and all 11 are named `Companion: <creature>`, and one carries `30030` and is named `Test Recipe`. A class
tag is exactly what varies that way; a file magic is not. (Only `0xC76B` is an instruction immediate anywhere in
the exe — `0xC73D` / `0x755F` / `0x754E` are not, so the client does not compare these; they are authoring-side
class ids.)

### ⭐ `a` is a structural COUNT, not a category

Measured by `--report spell-element-lists`, which cross-tabs `a` against what the record actually carries:

```
`a` == 2 + element lists + 1 if the record carries a [22][36] block   : 20,551 / 21,165 = 97.10%
```

So `a` counts the record's **value blocks**: one for the scalar pair region, one for the `[21][33]` text block,
one per `[2][listId]` element list, and one for the `[22][36]` list-valued pair. Its floor is exactly 2 — a
record with no element list and no list fields — on 1,995 of the 1,999 such records.

⚠️ **97.10% is a reading, not a definition**, and the cross-tab prints its own residual: 508 records come out one
short of their `a` (mostly `a=4` with one list and no block detected) and ~106 come out over. Both edges look
like the *detector* rather than the rule — the block-scan and the element-list tiling each fail on a handful —
but nothing has attributed them, so the table is printed row by row instead of a single percentage.

⭐ This **replaces** the old reading that `a` splits castables from internal effect records. That split is real
but derived: an internal effect record carries no element list, so it lands on the floor, and a castable carries
several. The correlation was with the record's *content*, not with a kind word.

⚠️ What is left is not bytes but ONE meaning: the flags word's unnamed bits. Every byte of `Hellfire Stream` is
inside a named region.

**The field ids are the same id space as wire stat ids** — `CharacterStatId` names them. That is what turns the
field run from a numbered blob into a readable property bag: most already have a client name, out of the
`GameAction_*` / LDB-615 runs. `--report rdb-field-coverage` is the live tally; a hard number here goes stale
(the "45 of 111" this line used to carry was counted against the pre-#632 field set).

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
dotnet run --project Faolan.Importer -- --report spell-fields --Content:RdbDir <rdb-dir>

# one spell's whole property run
dotnet run --project Faolan.Importer -- --report spell-fields --report-arg 4076785 --Content:RdbDir <rdb-dir>

# one field's value distribution, optionally crossed with another
dotnet run --project Faolan.Importer -- --report rdb-field-coverage --report-arg 1040005:186 --Content:RdbDir <rdb-dir>
dotnet run --project Faolan.Importer -- --report rdb-field-coverage --report-arg 1040005:347x348 --Content:RdbDir <rdb-dir>

# ...and with --out, EVERY carrying record rather than the 14 samples (id / name / value, one TSV per field) -
# what a histogram cannot answer is "is this the second source for a column we already fill", and that needs the
# per-record join (#633, field 210).
dotnet run --project Faolan.Importer -- --report rdb-field-coverage --report-arg 1040005:210 --Content:RdbDir <rdb-dir> --out <dir>
```
