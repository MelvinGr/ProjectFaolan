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
u32 state            (+0x10)
u32 field114         (+0x114)          # beach codec MISLABELS this as "Zero"
u32 field118         (+0x118)          # beach codec MISLABELS this as "N"
u32 n                (+0x14)  goal count; beach codec MISLABELS this "Marker" and ASSERTS ==0x412 (beach-only!)
string title, summary, area, region1, region2   # cats 50301/50302/50300/52000/52000
u32 fieldEc          (+0xec)
u32 areaId           (+0xcc)
u32 field2c          (+0x2c)           # beach codec MISLABELS this "questId2" and ASSERTS ==questId (beach-only!)
u32 field11c         (+0x11c)
u64 accept           (+0x20)
u32 field28          (+0x28)
objlist obj1, obj2                     # each: [u32 count][count × ObjRec]
u32 fieldF0          (+0xf0)
u64 fieldF8          (+0xf8)
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
