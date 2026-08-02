# Quest_c region (0x737A6DF9 FullChar snapshot) — deep decode (2026-07-05)

The Quest_c region is the quest JOURNAL at the front of the snapshot tails. `QuestcRegionCodec` models it and
round-trips the **beach create template** byte-exact, but the beach template is the *simplest* possible journal
(fresh char: full quests, empty objective lists, no progress). This doc records the FULL structure recovered
from the client reader (Ghidra) + validated against NecroL80's lvl80 login (27 quests, real progress), so the
generator can eventually emit arbitrary journals.

## Readers (Ghidra `AgeOfConan.exe`, project `tools/ghidra/output`)

- `FUN_0091f5c0` — **List-A driver**: reads `u32 count = 0x3F1*(records+1)`, then loops `records` times, each
  iteration reading one `u32` (the record's listKey) + calling the per-record reader.
- `FUN_0091df50` — **per-record reader**. Read primitives: `FUN_0048f900`=ReadU32, `FUN_0048f840`=ReadString
  (`[u16 len][utf8]`), `FUN_0048f720`=ReadU64, `FUN_00cc0540`=Read4×U32. Post-obj4 calls are `FUN_009202d0`
  (std::sort introsort) + vector-push, NOT stream reads.

## `ACGHash` — the 4-character token, and why it is never a magic

`FUN_005d7190` is the client's **ACGHash** stream reader, and it is what every "4-character magic" in the quest
packets actually goes through. It reads one `u32`, checks each of the four bytes against `[0-9A-Z_]`, and on a
mismatch logs `Read invalid hash "<...>" from stream` under the `ACGHash` debug channel. It compares against
**nothing** — there is no expected value, because the value is an identity token, not a marker.

That names three things this repo had decoded separately as random tokens:

- the `QuestJournalIIR` word read as a `"RRL3"` **record-list magic** — really the AREA's token (#544);
- `QuestObjectiveGoal.Reserved`'s tokens, incl. the `WTHD` block below (#546);
- the quest index's per-block `magic` (`QuestDefinition.IndexMagic`), which is the same area token again — a
  quest's index block and its journal block carry one value, keyed by `AreaId`.

⚠️ So: **a four-byte field of printable uppercase-alphanumerics in a quest packet is an ACGHash.** Asserting the
one value the first decoded capture carried is the same mistake as asserting a `0x3F1` list frame, and it failed
the same way — 31 of 38 captured journals rejected.

Observed area tokens: 241 → `W6QT`, 1054 → `RRL3`, 1772 → `B0DN`, 1834 → `3734`.

## `QuestJournalIIR` (0x5A2FE45E) — reader `FUN_0091a4e0` (vtable slot 8 of `QuestJournalIIR_c`)

```
[u32 subop][u32 class][u32 playerHandle][u8 0x01]
<area block>                                FUN_00922c90
[u32 currentFlag]                           0 / 1
[ListFrame'd list of <area block>]          FUN_00798c70 — frame-ONLY, empty in all 38 captures
[u32 currentQuestId]                        0 = none
[ListFrame'd list of strings]               FUN_004d55b0 — the named quest's title + sub-line
[u32 currentOrdinal]                        0 = none
```

with the area block (`FUN_00922c90`)

```
[u32 areaId][string areaTitle][string mapName][string regionName]
[u32 acgHash][u32 unknown=0][u32 count]
count x [u32 ordinal][u32 questId][ListFrame'd list of 2 strings: title, sub-line]
```

Two notes the corpus alone could not settle. **The trailing five fields are not a "tracked-quest pointer".** They
had been read as `[u32 trackedQuestId][list A][list B]` with each list `[frame][count]`, which only fits a
one-quest journal: in every larger one, list A's "count" slot holds a quest id and list B's frame holds `0xBD3`.
The reader shows five independent fields instead, and both captured shapes then fall out exactly — `(0, empty, 0,
empty, 0)` for a single-quest journal and `(1, empty, questId, 2 strings, entryCount)` naming the LAST entry for
every larger one. **And a string list here is frame-ONLY** — one `(count+1) * 0x3F1` word and then the strings,
with no separate count word, unlike the record lists which carry both. That is why the per-entry `0xBD3` read as a
"ChapterTag": it is `ForCount(2)`, framing the entry's title + sub-line.

## Record grammar (validated byte-exact on NecroL80 rec0, snapshot offset 11024)

```
[listKey=questId]
u32 ver(=1)
u32 questId          (+0xc)
u32 level            (+0x10)  the quest LEVEL (see below); modelled as QuestcRecord.Level
u32 field114         (+0x114)          # beach codec MISLABELS this as "Zero"
u32 field118         (+0x118)          # beach codec MISLABELS this as "N"
u32 lineFlags        (+0x14)  a flag word of the quest LINE - NOT a goal count (see below)
string title, summary, area, region1, region2   # cats 50301/50302/50300/52000/52000
u32 fieldEc          (+0xec)
u32 areaId           (+0xcc)
u32 field2c          (+0x2c)           # beach codec MISLABELS this "questId2" and ASSERTS ==questId (beach-only!)
u32 field11c         (+0x11c)
u64 money            (+0x20)  the quest's MONEY reward, in TIN (see below)
u32 xpReward         (+0x28)  the quest's EXPERIENCE reward (see below)
objlist obj1, obj2                     # each: [u32 count][count × ObjRec]
u32 fieldF0          (+0xf0)  NOT the journal's XP (see below)
u64 fieldF8          (+0xf8)  NOT the journal's Money (see below)
objlist obj3, obj4
progress-sublist                       # <-- the piece the beach template never exercises
```

`ObjRec` = **16 u32** on the wire (reader scrambles them into an 18-word struct: 2 words, then `Read4×U32`,
2 words, `Read4×U32`, 2 words). Reading them as 16 raw u32 in wire order (what `QuestcRecordCodec` does) IS
byte-exact.

## ~~progress-sublist (per record, after obj4) — the real blocker~~ — RESOLVED 2026-07-20 (#384)

> **This section's conclusion was wrong.** What follows below is kept for the record; read this first.
>
> There is no "progress sub-list" and no opaque server block. The thing after `obj4` is the quest's own **@609
> objective region** — `QuestObjectiveRegion`, a codec this repo already had — and it is **interleaved per
> quest record**, not appended once after the whole list. `FullCharSnapshotSubClass` had always *written* it
> that way; only the reader was wrong, treating it as a separate top-level region.
>
> That parses the beach template fine because the beach journal holds exactly **one** quest, where "after the
> record" and "after the last record" are the same offset — the same single-element blind spot as the
> [`0x7E2` list-frame trap](../protocol/README.md). On any real journal the reader desynced at record 2 and
> threw `Quest_c record: marker 0x2 != 0x412`.
>
> The `WTHD` "magic" was a **mis-segmentation**: those bytes are `QuestObjectiveGoal.Reserved[5..6]`, the pair
> this repo already documented as "70099 / a 4-char tag". It has zero xrefs in `AgeOfConan.exe` not because it
> is opaque server data, but because **it is not a constant at all** — it is a random 4-character token,
> distinct in every entry (NecroL80's journal: 39 anchors, 39 distinct tokens — `WTHD`, `1LJL`, `UFWN`, `HIB4`,
> `C9PY`, …), the same flavour of random key the `<remoteformat key="...">` attributes carry.
>
> Verified by `ProgressedJournalSnapshotTests` across **all 141 captured snapshots** (beach L1,
> L23, L24, and both L80 journals), and gated in the suite by `ProgressedJournalSnapshotTests` — 141 bodies,
> 85 of them multi-quest, tails byte-exact.

## progress-sublist (per record, after obj4) — the original (incorrect) reading

`[u32 frame = 0x3F1*(k+1)][k × progress-entry]`. rec0 (quest 13907): `frame=0x7E2 ⇒ k=1`. Full byte map of the
one entry (offsets from NecroL80's snapshot):

```
+0  u32 flag        = 1
+4  u32 goalCount    = 12 (0xC)
+8  u32 playerHandle = 0x0802E5D4          # the character's own handle
+12 u32 questId      = 13907
+16 u32 goalId       = 6451
+20 u32              = 1
+24 u32 goalId       = 6451
+28 u32              = 1
+32 5× u32 = 0
+52 u32 id           = 70099 (0x111D3)
+56 'WTHD' (0x57544844) block: [WTHD][0][3042][171][171][0][0]   # server data, see below
+84 u32 strlen16?    = 100 ; then a remoteformat string id=6451 category=50304 (the GOAL description)
    then a second string: [u16 len=81] <localized id="8235" category="51000" flags="m" …>
    then ~7 more u32 fields (0x34A1.. flags/timestamps) before the next quest record
```

### Why this can't be fully generated from client RE

The **`WTHD` block is opaque server data**: the magic `0x57544844` is referenced NOWHERE in the client
(`FindConst` = 0 hits in either byte order), so there is no client reader to decompile for its internal
field semantics — it is stored/echoed as a blob. This is the same class of blocker as the server-held RDB
keys: the client never *interprets* these bytes, so their meaning isn't recoverable from `AgeOfConan.exe`.
Records are also VARIABLE (rec1 quest 6451 has empty header strings + is itself a goal-style entry), so a
faithful arbitrary-journal generator needs either a retail server-side spec or a large corpus of progressed
captures to infer the WTHD/progress fields empirically.

## Status — DONE (#384, 2026-07-20)

Both items below shipped; nothing here is blocked any more.

- The mislabeled fields were renamed (`Zero→Field114`, `N→Field118`, `Marker→N`, `QuestId2→Field2c`) and the
  two beach-only asserts (`!=0x412`, `questId2!=questId`) dropped. Read/write order is unchanged, so every
  beach golden stayed byte-exact. `QuestcRegion.Marker` is now `QuestcRecord.Unknown14` — the value the beach
  template happens to carry in the goal-count slot, never something to assert against.
- The per-record objective region is modelled as `QuestcRecord.Objectives` and read/written with the record,
  so `QuestcRegion` round-trips a progressed journal. `FullCharSnapshotSubClass` lost its now-redundant
  top-level `QuestObjectives` property (the records own it) and no longer re-parses that region at the
  boundary.

Reading a progressed journal was never blocked by a spec or a corpus — the structure was already fully
decoded, just attributed to the wrong owner. The lesson is the recurring one: **a grammar pinned only by the
single-element/degenerate capture is not pinned**, and a "constant" that appears once per record is worth
checking against a second capture before calling it a magic.

## Static RE pass on the record's unnamed fields (2026-08-01)

What the client can and cannot tell us about `@+0x14`, `@+0x28`, `@+0xf0`, `@+0xf8` and `@+0x11c`. The short
answer: the STRUCTURE is confirmed from both sides, and the NAMES are not in the binary — anyone continuing
this should trace a consumer, not hunt for another string.

**The class.** RTTI is intact: the type descriptor `.?AVQuest_c@@` is at `021cf360`, its complete-object
locator at `020dc260`, and the vtable at `01fdfd18` — only three virtuals (`FUN_0091d090`, `FUN_0091ce50`,
`FUN_0091ce60`). Its source file is `quest.cpp`; the record's string pool sits right after the vtable.

**Reader and writer agree with our codec.** `FUN_0091df50` reads the record and `FUN_0091d260` writes it back
(the byte-swap-then-emit pattern), and between them they touch exactly the offsets `QuestcRecordCodec` models,
in the order it reads them — including `@+0x20`/`@+0x24` and `@+0xf8`/`@+0xfc` as the two u64 pairs. The reader
is NOT virtual (no pointer to it exists anywhere in the image) and has exactly two callers, `FUN_0091f5c0` and
`FUN_00917e20`, both thin parse-loop wrappers that never touch the fields.

**Why the names are not recoverable by string anchor.** The writer is label-free — it emits scalars with no
per-field assert, unlike e.g. `DispatchSetQuestMarker`, whose asserts name `i_cNPC` and `i_eMarker`. The quest
module's string pool names plenty of METHODS and one member, but nothing on this record:

| string | what it gives |
|---|---|
| `QuestHolder_t::UpdateCurrentProgress() m_pcCurrentQuest is NULL.` | the holder class + its member |
| `QuestGoal_c::SetParent()` / `SetSolvedTimes()` / `GetQuestLevel()` | goal API; "SolvedTimes" is the goal's progress count |
| `QuestGoal_c::GetQuestLevel() … Returning Stat value` / `Returning Quest value` | the goal's level resolves from a STAT first, the quest second |
| `MainQuest_c::WriteToStream` | `mainquest.cpp` is a separate class from `quest.cpp` |
| `QuestRewardNotifyIIR_c`, `QuestRewardSelectionIIR_c` | rewards have their OWN IIRs |
| `QuestJournalIIR_c::ReadSubClass()`, `PatrolIIR_c`, `RDBQuestGivers` | neighbouring subsystems |

Two of those bear on the open question directly. Rewards being delivered by their own IIRs is evidence
AGAINST reading the level-scaled `@+0x28`/`@+0xf0`/`@+0xf8` as a reward carried on the journal record — it is
still the best fit for their magnitudes, but the client does not obviously need them there. And
`GetQuestLevel()` returning "the Stat value" or "the Quest value" says a goal's level has two sources, which is
the kind of level-adjacent pairing `@+0x118` (level..level+5) already hints at.

**Exhausted anchors, so don't repeat them:** the vtable (3 slots, none of them a getter), the reader's callers,
a whole-image scan for `[reg + 0xf0]` (778 functions — the offset is far too common to filter on), and the
module's strings. What is left is the semantic route: follow a parsed record from `FUN_0091f5c0` into the
journal UI and find the code that formats these words for display.

## What the unnamed fields ARE (2026-08-01, from the client's quest catalog)

The static pass above ended with "the names are not in the binary — trace a consumer". They were not in the
binary; they were in the client's **data**. Cross-referencing the captured records against RDB type 1000104
([quest-catalog-1000104.md](../rdb/quest-catalog-1000104.md)) named three of the five and pinned the scope of
a fourth. The probe is 2626 captured records over 141 snapshots, covering 99 distinct quests.

**The control that made this tractable: every one of these words is DEFINITION data.** Per quest, across all
2626 records and every captured character, `@+0x14`, `@+0x28`, `@+0x2c`, `@+0xec`, `@+0x114`, `@+0x118` and
`@+0x11c` never vary; `@+0xf0`/`@+0xf8` vary on exactly one quest (10238, the known two-spelling variant) and
`@+0x10` on three. 97 of the 99 quests appear in more than one capture, so this is not a single-source
artefact. A definition field has a definition-side source, which is what pointed at the catalog.

### `@+0x10` — the quest LEVEL, not a state

It equals the DB `Level` on 96 of the 99 captured quests (12373, 17402 and 17403 differ by 1–2, i.e. the row
took its level from a different capture). Both writers already fed it a level. It was modelled as
`QuestcRecord.State` until this pass, which made `QuestcRegionTests` assert `Equal(1u, q.State) // active` on
the beach starter — passing only because that quest happens to be **level 1**. Distinct from
`QuestCodec.State` (`StateActive`/`StateCompleted`), a real state on a different message.

### `@+0x28` — the quest's EXPERIENCE reward (settled IN-CLIENT)

The client's journal renders a quest's detail pane with an `XP:` and a `Money:` line, and `XP:` is this field.

[V1: Faolan.ClientLauncher/Program.cs — driven live against a local server: quest 13227's column was set to
5553, then 0, then 7777, relogging between each, and the panel read back `XP: 5553`, `XP: 0`, `XP: 7777` with
nothing else changed. In the same runs `@+0xf0 = 8888` and `@+0xf8 = 999999` produced no change to either
label — `Money:` stayed blank.]

Its positional law still holds and is still unexplained: `@+0x28` is populated on the LAST step of a quest
line (25 of 26 captured cases) and `@+0xf0`/`@+0xf8` never are. It scales with level — level 23 → 900..4981,
level 30 → 5553, level 70 → 15421, level 80 → 69337..138674, the level-80 values being exactly
{1, 4/3, 5/3, 2} × 69337.

### `@+0x20` — the quest's MONEY reward (settled IN-CLIENT)

The twin of `@+0x28`: a u64 money at 0x20 and a u32 XP at 0x28, adjacent in the struct, and the two lines the
journal's detail pane prints as `Money:` and `XP:`.

[V1: Faolan.ClientLauncher/Program.cs — driving it live on a rendered quest, 111200 rendered `11S 12C` and
250000 rendered `25S`; the harvested 214 of quest 13507 renders `2C 14T`. So the unit is TIN, with 100 tin =
1 copper and 100 copper = 1 silver.]

⚠️ It was modelled as `Accept`, "likely acceptTime", and **emitted as a constant 0** — so every captured value
was thrown away. 21 of the 99 captured quests carry one, constant per quest across every character and
capture, and that set is a SUBSET of the quests carrying an XP reward: some quests pay experience only.

At level 80 the harvested pairs run money ≈ **1.10 ×** xp (152546/138674, 127115/115562, 101684/92449). That
is the same ratio `@+0xf8`/`@+0xf0` shows at level 80, which is the strongest hint yet that those two are the
same (xp, money) pair at a different scope — see below — though still not proof.

### `@+0xf0` / `@+0xf8` — NOT the journal's XP or Money

⚠️ **An earlier version of this document named these two `XpReward` and `CashReward`.** That was inferred from
the client catalog's `xp_reward`/`cash_reward` pair plus a magnitude coincidence, and the in-client test above
disproved it: with both set to large distinctive values the quest pane showed the XP from `@+0x28` and left
`Money:` blank. **A field-name match in a neighbouring data file is not evidence about the wire.**

What remains true, and unexplained: the catalog gives a quest LINE exactly one `xp_reward`/`cash_reward` pair,
and the magnitudes coincide — the `[Thunder River]` lines carry `xp_reward = 45800`, exactly the captured
`@+0xf0` of quests 13895 and 14427, and the catalog's low-level cash/xp ratios (715/60, 1000/157, 455/36 →
0.08–0.16) match the wire's `@+0xf8`/`@+0xf0` (150/15, 370/44, 550/66 → 0.10–0.12). A LINE-level reward the
step pane never renders would fit both observations — but that is a guess, and the last guess here was wrong,
so the two keep their offsets as names.

⚠️ `@+0xf8` tracks `@+0xf0` through a level-dependent factor (~0.267 at level 23 rising to ~1.10 at 80), but 99
samples do not close the law. Do not derive one from the other.

`@+0xf0` additionally selects WHICH SPELLING the quest's @609 objective region uses for its trailing text —
see the `ObjectiveRegionVerified` note on `QuestDefinition`.

### `@+0x14` — a bitfield of the quest LINE; bit 4 = NOT SHAREABLE

Five values across the corpus (2, 10, 18, 26, 1042), every one ≡ 2 (mod 8): bits 0–2 fixed `010`, bits 3, 4
and 10 toggling. Definitively **not** a goal count — 2 appears on records carrying 1, 2, 3 and 6 goals.

[V1: Faolan.ClientLauncher/Program.cs — its SCOPE is the line, not the quest: the value is constant across
every quest of the same line in all **90** lines that have a captured quest, with zero conflicts, and constant
per quest across 2626 captured records.]

**bit 4 (0x10) = the quest may not be SHARED.**

[V1: Faolan.ClientLauncher/Program.cs — swept live: one rendered quest's column was driven through all five
observed values, relogging each time, and the journal's detail pane greys out its `Share` button for exactly
the three values with bit 4 set (18, 26, 1042) and enables it for the two without (2, 10). The captures within
each class are PIXEL-IDENTICAL, which isolates bit 4 — bit 3 differs between 2 and 10 and between 18 and 26,
and changes nothing.]

The corpus fits the meaning: bit 4 is set on the Destiny steps, the Alchemist tier-4 progression and the
Khitai/faction content (37 of the 82 eligible quests), and clear on ordinary side quests (45) — personal-story,
crafting-progression and faction content being non-shareable is exactly the expected rule.

**bits 3 (0x08) and 10 (0x400) — no effect on the journal panel.** The same sweep rendered 2 vs 10, and 18 vs
26 vs 1042, pixel-identically, so whatever they drive is not this UI.

⚠️ Two earlier readings are superseded. Bit 4 was guessed as "expansion content", which had counterexamples
(17402/17403 are in Khitai with it clear); it is sharing, and those are simply shareable Khitai quests. Bit 10
was read as marking "player-progression lines" on a correlation with the Destiny/Alchemist/Mastery set — that
correlation stands, but the sweep shows it drives nothing in the journal, so it remains unexplained.

⚠️ Do not read the quest TREE pane in a sweep like this. The client persists its expanded/collapsed and scroll
state across relogs, so the left pane drifts between runs independently of the data — it produced three
apparent "classes" that map to no bit at all. Only the detail pane is deterministic.

### `@+0x11c` — 1 on exactly the quests absent from the client's quest-giver catalog

1 for 19314, 19364, 19365, 19522 and 19620 (World Bosses, Anniversary 2014, Halloween 2015, Onslaught), 0 for
the other 94.

A second, independent signal picks out the same set: those are exactly the captured quests that have **no
entry in RDB 1000104**, the client's quest-giver catalog (the sixth such quest, 19363, has no captured journal
record at all, so the wire never stated a `@+0x11c` for it). Among the 99 quests we can see both sides of, the
two agree perfectly.

That suggests the flag marks a quest reached some way OTHER than a quest giver — script-granted, event, meta,
raid-finder — which fits what those five are. ⚠️ It is a HYPOTHESIS: n = 5, and the catalog omits 952 of the
client's 5920 quests for reasons that are themselves not established (see
[quest-catalog-1000104.md](../rdb/quest-catalog-1000104.md)), so the correlation may be inherited from
whatever really drives that omission rather than being the field's meaning.

⚠️ An earlier version of this note explained the same observation as "content newer than the RDB". That was
wrong — the client is the same build the captures came from, and its own LDB carries the text for those
quests.

## Synthesising a Quest_c record — the snapshot ceiling, 82 → 99

`SnapshotTails.IsSnapshotEligible` wants five things: the record scalars, a non-empty goal list, a verified
objective region, the line's 4CC, and the five header text refs. Seventeen quests had **everything but the
record** — no capture ever carried one for them — so they sat out of the login snapshot.

`ImportOps.SeedDerivedSnapshotRecords` derives one, running after `VerifyObjectiveRegions` because that
verdict is its gate. It re-tests every other arm of `IsSnapshotEligible`, so it can never flag a quest the
gate would reject. What it fills:

| field | derived from |
|---|---|
| `@+0x14` flags | the line — a captured sibling states it for the whole line; else 2, the modal value and bit-4-clear (shareable) |
| `@+0x118` | the quest's `Level` (captured values run level..level+5; the level is that range's low end) |
| `@+0xec` | 1 — constant in all 99 captured records |
| `@+0x28`, `@+0xf0`, `@+0xf8` | left 0 |
| `@+0x2c` chain head | left 0; `QuestcRecords.ForQuest` already falls back to the quest's own id |

⚠️ This SYNTHESISES wire data, which the gate's own comment warns against. What makes it defensible is that
the record's **shape** is fixed and only its **values** are invented, and each invented value was proven
harmless in-client before being shipped: `@+0x14` renders without complaint at all five observed values (only
the `Share` button moves, on bit 4), `@+0x28` is the printed XP so 0 renders "XP: 0", and `@+0xf0`/`@+0xf8`
changed nothing visible at any value tried.

[V1: Faolan.ClientLauncher/Program.cs — verified on a live login: character Hakotep holds 21 snapshot-eligible
active quests of which **5 are derived records**, and the client's journal header reads `Quests (21/45)` — the
exact count — with the derived quests rendered and **no `statlist.cpp:134` assert** in the client log. The
hazard the gate guards against is real for the objective REGION; the record scalars are safe to derive.]

⚠️ The cost is cosmetic and visible: a derived quest shows **`XP: 0`**, because `@+0x28` is not recoverable for
a quest no capture carried. It is wrong on screen, not wrong on the wire.

**This does not generalise far.** The remaining 4940 quests are not blocked by the record — they have no goal
data at all (only 108 quests have wire goals, and only 99 a captured @609 region to verify against). The limit
is capture coverage of the objective region, not decode.
